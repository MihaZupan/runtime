// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace System.Net.Http
{
    internal abstract class HttpBaseStream : Stream
    {
        public sealed override bool CanSeek => false;

        public sealed override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state) =>
            throw new NotImplementedException();

        public sealed override int EndRead(IAsyncResult asyncResult) =>
            throw new NotImplementedException();

        public sealed override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state) =>
            throw new NotImplementedException();

        public sealed override void EndWrite(IAsyncResult asyncResult) =>
            throw new NotImplementedException();

        public sealed override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public sealed override void SetLength(long value) => throw new NotSupportedException();

        public sealed override long Length => throw new NotSupportedException();

        public sealed override long Position
        {
            get { throw new NotSupportedException(); }
            set { throw new NotSupportedException(); }
        }

        public sealed override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotImplementedException();
        }

        public sealed override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return ReadAsync(new Memory<byte>(buffer, offset, count), cancellationToken).AsTask();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotImplementedException();
        }

        public sealed override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return WriteAsync(new ReadOnlyMemory<byte>(buffer, offset, count), cancellationToken).AsTask();
        }

        public override void Flush() => FlushAsync(default).GetAwaiter().GetResult();

        public override Task FlushAsync(CancellationToken cancellationToken) => NopAsync(cancellationToken);

        protected static Task NopAsync(CancellationToken cancellationToken) =>
            cancellationToken.IsCancellationRequested ? Task.FromCanceled(cancellationToken) :
            Task.CompletedTask;

        //
        // Methods which must be implemented by derived classes
        //

        public abstract ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);
        public abstract ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken);
    }
}
