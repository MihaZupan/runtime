// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using Microsoft.DotNet.RemoteExecutor;
using Xunit;

namespace System.Security.Claims
{
    public class ClaimsPrincipalTests
    {
        [Fact]
        public void Ctor_Default()
        {
            var cp = new ClaimsPrincipal();

            Assert.NotNull(cp.Identities);
            Assert.Equal(0, cp.Identities.Count());

            Assert.NotNull(cp.Claims);
            Assert.Equal(0, cp.Claims.Count());

            Assert.Null(cp.Identity);
        }

        [Fact]
        public void Ctor_IIdentity()
        {
            var id = new ClaimsIdentity(
                       new List<Claim> { new Claim("claim_type", "claim_value") },
                       "");
            var cp = new ClaimsPrincipal(id);

            Assert.NotNull(cp.Identities);
            Assert.Equal(1, cp.Identities.Count());

            Assert.Same(id, cp.Identities.First());

            Assert.Same(id, cp.Identity);

            Assert.NotNull(cp.Claims);
            Assert.Equal(1, cp.Claims.Count());
            Assert.Contains(cp.Claims, claim => claim.Type == "claim_type" && claim.Value == "claim_value");
        }

        [Fact]
        public void Ctor_IIdentity_NonClaims()
        {
            var id = new NonClaimsIdentity() { Name = "NonClaimsIdentity_Name" };
            var cp = new ClaimsPrincipal(id);

            Assert.NotNull(cp.Identities);
            Assert.Equal(1, cp.Identities.Count());

            Assert.NotSame(id, cp.Identities.First());
            Assert.NotSame(id, cp.Identity);
            Assert.Equal(id.Name, cp.Identity.Name);

            Assert.NotNull(cp.Claims);
            Assert.Equal(1, cp.Claims.Count());
            Assert.Contains(cp.Claims, claim => claim.Type == ClaimsIdentity.DefaultNameClaimType && claim.Value == "NonClaimsIdentity_Name");
        }

        [Fact]
        public void Ctor_IPrincipal()
        {
            var baseId = new ClaimsIdentity(
                           new List<Claim> { new Claim("claim_type", "claim_value") },
                           "");
            var basePrincipal = new ClaimsPrincipal();
            basePrincipal.AddIdentity(baseId);

            var cp = new ClaimsPrincipal(basePrincipal);

            Assert.NotNull(cp.Identities);
            Assert.Equal(1, cp.Identities.Count());

            Assert.Same(baseId, cp.Identities.First());

            Assert.Same(baseId, cp.Identity);

            Assert.NotNull(cp.Claims);
            Assert.Equal(1, cp.Claims.Count());
            Assert.Contains(cp.Claims, claim => claim.Type == "claim_type" && claim.Value == "claim_value");
        }

        [Fact]
        public void Ctor_NonClaimsIPrincipal_NonClaimsIdentity()
        {
            var id = new NonClaimsIdentity() { Name = "NonClaimsIdentity_Name" };
            var basePrincipal = new NonClaimsPrincipal { Identity = id };
            var cp = new ClaimsPrincipal(basePrincipal);

            Assert.NotNull(cp.Identities);
            Assert.Equal(1, cp.Identities.Count());

            Assert.NotSame(id, cp.Identities.First());
            Assert.NotSame(id, cp.Identity);
            Assert.Equal(id.Name, cp.Identity.Name);

            Assert.NotNull(cp.Claims);
            Assert.Equal(1, cp.Claims.Count());
            Assert.Contains(cp.Claims, claim => claim.Type == ClaimsIdentity.DefaultNameClaimType && claim.Value == "NonClaimsIdentity_Name");
        }

        [Fact]
        public void Ctor_NonClaimsIPrincipal_NoIdentity()
        {
            var p = new ClaimsPrincipal(new NonClaimsPrincipal());
            Assert.NotNull(p.Identities);
            Assert.Equal(1, p.Identities.Count());

            Assert.NotNull(p.Claims);
            Assert.Equal(0, p.Claims.Count());

            Assert.NotNull(p.Identity);
            Assert.False(p.Identity.IsAuthenticated);
        }

        [Fact]
        public void Ctor_IPrincipal_NoIdentity()
        {
            var cp = new ClaimsPrincipal(new ClaimsPrincipal());
            Assert.NotNull(cp.Identities);
            Assert.Equal(0, cp.Identities.Count());

            Assert.NotNull(cp.Claims);
            Assert.Equal(0, cp.Claims.Count());

            Assert.Null(cp.Identity);
        }

        [Fact]
        public void Ctor_IPrincipal_MultipleIdentities()
        {
            var baseId1 = new ClaimsIdentity("baseId1");
            var baseId2 = new GenericIdentity("generic_name", "baseId2");
            var baseId3 = new ClaimsIdentity("customType");

            var basePrincipal = new ClaimsPrincipal(baseId1);
            basePrincipal.AddIdentity(baseId2);
            basePrincipal.AddIdentity(baseId3);

            var cp = new ClaimsPrincipal(basePrincipal);
            Assert.NotNull(cp.Identities);
            Assert.Equal(3, cp.Identities.Count());

            Assert.NotNull(cp.Claims);
            Assert.Equal(1, cp.Claims.Count());

            Assert.Equal(baseId1, cp.Identity);

            Assert.Contains(cp.Claims, claim => claim.Type == ClaimsIdentity.DefaultNameClaimType && claim.Value == "generic_name");

            Assert.Equal(baseId2.Claims.First(), cp.Claims.First());
        }

        [Fact]
        public void Ctor_IEnumerableClaimsIdentity_Empty()
        {
            var cp = new ClaimsPrincipal(new ClaimsIdentity[0]);
            Assert.NotNull(cp.Identities);
            Assert.Equal(0, cp.Identities.Count());
            Assert.NotNull(cp.Claims);
            Assert.Equal(0, cp.Claims.Count());
            Assert.Null(cp.Identity);
        }

        [Fact]
        public void Ctor_IEnumerableClaimsIdentity_Multiple()
        {
            var baseId1 = new ClaimsIdentity("baseId1");
            var baseId2 = new GenericIdentity("generic_name2", "baseId2");
            var baseId3 = new GenericIdentity("generic_name3", "baseId3");

            var cp = new ClaimsPrincipal(new List<ClaimsIdentity> { baseId1, baseId2, baseId3 });
            Assert.NotNull(cp.Identities);
            Assert.Equal(3, cp.Identities.Count());

            Assert.NotNull(cp.Claims);
            Assert.Equal(2, cp.Claims.Count());

            Assert.Equal(baseId1, cp.Identity);

            Assert.Contains(cp.Claims, claim => claim.Type == ClaimsIdentity.DefaultNameClaimType && claim.Value == "generic_name2");
            Assert.Contains(cp.Claims, claim => claim.Type == ClaimsIdentity.DefaultNameClaimType && claim.Value == "generic_name3");

            Assert.Equal(baseId2.Claims.First(), cp.Claims.First());
            Assert.Equal(baseId3.Claims.Last(), cp.Claims.Last());
        }

        [Fact]
        public void Ctor_ArgumentValidation()
        {
            AssertExtensions.Throws<ArgumentNullException>("identities", () => new ClaimsPrincipal((IEnumerable<ClaimsIdentity>)null));
            AssertExtensions.Throws<ArgumentNullException>("identity", () => new ClaimsPrincipal((IIdentity)null));
            AssertExtensions.Throws<ArgumentNullException>("principal", () => new ClaimsPrincipal((IPrincipal)null));
            AssertExtensions.Throws<ArgumentNullException>("reader", () => new ClaimsPrincipal((BinaryReader)null));
        }

        [ConditionalFact(typeof(RemoteExecutor), nameof(RemoteExecutor.IsSupported))]
        public void Current_FallsBackToThread_NoPrincipalPolicy()
        {
            RemoteExecutor.Invoke(() =>
            {
                ClaimsPrincipal principal1 = new ClaimsPrincipal();
                ClaimsPrincipal principal2 = new ClaimsPrincipal();

                Thread.CurrentPrincipal = principal1;
                Assert.Same(principal1, ClaimsPrincipal.Current);

                Thread.CurrentPrincipal = principal2;
                Assert.Same(principal2, ClaimsPrincipal.Current);

                NonClaimsIdentity id = new NonClaimsIdentity() { Name = "NonClaimsIdentity_Name" };
                NonClaimsPrincipal nonClaimsPrincipal = new NonClaimsPrincipal() { Identity = id };

                Thread.CurrentPrincipal = nonClaimsPrincipal;

                ClaimsPrincipal current = ClaimsPrincipal.Current;
                Assert.NotNull(current);
                Assert.Equal("NonClaimsIdentity_Name", current.Identity.Name);

                Thread.CurrentPrincipal = null;
                Assert.Null(ClaimsPrincipal.Current);
            }).Dispose();
        }

        [ConditionalFact(typeof(RemoteExecutor), nameof(RemoteExecutor.IsSupported))]
        public void Current_FallsBackToThread_UnauthenticatedPrincipalPolicy()
        {
            RemoteExecutor.Invoke(() =>
            {
                AppDomain.CurrentDomain.SetPrincipalPolicy(PrincipalPolicy.UnauthenticatedPrincipal);
                Thread.CurrentPrincipal = null;
                Assert.IsType<GenericPrincipal>(ClaimsPrincipal.Current);
            }).Dispose();
        }

        [ConditionalFact(typeof(RemoteExecutor), nameof(RemoteExecutor.IsSupported))]
        public void ClaimsPrincipalSelector_DefaultNull()
        {
            RemoteExecutor.Invoke(static () =>
            {
                Assert.Null(ClaimsPrincipal.ClaimsPrincipalSelector);
            }).Dispose();
        }

        [ConditionalFact(typeof(RemoteExecutor), nameof(RemoteExecutor.IsSupported))]
        public void ClaimsPrincipalSelector_Roundtrip()
        {
            RemoteExecutor.Invoke(static () =>
            {
                ClaimsPrincipal selected = new();
                ClaimsPrincipal.ClaimsPrincipalSelector = () => selected;
                Assert.Same(selected, ClaimsPrincipal.ClaimsPrincipalSelector());
            }).Dispose();
        }

        private class NonClaimsPrincipal : IPrincipal
        {
            public IIdentity Identity { get; set; }

            public bool IsInRole(string role)
            {
                throw new NotImplementedException();
            }
        }
    }
}
