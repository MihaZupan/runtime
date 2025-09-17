// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;

namespace System.Configuration
{
    public class StringValidator : ConfigurationValidatorBase
    {
        private readonly SearchValues<char> _invalidChars;
        private readonly int _maxLength;
        private readonly int _minLength;

        public StringValidator(int minLength)
            : this(minLength, int.MaxValue, null)
        { }

        public StringValidator(int minLength, int maxLength)
            : this(minLength, maxLength, null)
        { }

        public StringValidator(int minLength, int maxLength, string invalidCharacters)
        {
            _minLength = minLength;
            _maxLength = maxLength;
            _invalidChars = SearchValues.Create(invalidCharacters ?? string.Empty);
        }

        public override bool CanValidate(Type type)
        {
            return type == typeof(string);
        }

        public override void Validate(object value)
        {
            ValidatorUtils.HelperParamValidation(value, typeof(string));

            string data = value as string;
            int len = data?.Length ?? 0;

            if (len < _minLength)
                throw new ArgumentException(SR.Format(SR.Validator_string_min_length, _minLength));
            if (len > _maxLength)
                throw new ArgumentException(SR.Format(SR.Validator_string_max_length, _maxLength));

            // Check if the string contains any invalid characters
            if (data.AsSpan().ContainsAny(_invalidChars))
            {
                throw new ArgumentException(SR.Format(SR.Validator_string_invalid_chars, _invalidChars));
            }
        }
    }
}
