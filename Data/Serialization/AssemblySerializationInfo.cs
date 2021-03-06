﻿using System;
using System.Text;

namespace Dasync.Serialization
{
    public sealed class AssemblySerializationInfo
    {
        private static readonly Version EmptyVersion = new Version(0, 0, 0, 0);

        public string Name;
        public Version Version;
        public string Token;

        public override bool Equals(object obj)
        {
            if (obj is AssemblySerializationInfo info)
                return this == info;
            return false;
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hashcode = 0;
                if (Name != null)
                    hashcode = Name.GetHashCode();
                if (Version != null && (Version.Major != 0 || Version.Minor != 0 || Version.Build != 0 || Version.Revision != 0))
                    hashcode = hashcode * 104651 + Version.GetHashCode();
                if (Token != null && Token.Length > 0)
                    hashcode = hashcode * 104651 + Token.GetHashCode();
                return hashcode;
            }
        }

        public static bool operator ==(AssemblySerializationInfo info1, AssemblySerializationInfo info2)
        {
            if (info1 is null && info2 is null)
                return true;

            if (info1 is null || info2 is null)
                return false;

            return info1.Name == info2.Name
                && (info1.Version ?? EmptyVersion) == (info2.Version ?? EmptyVersion)
                && (info1.Token ?? string.Empty) == (info2.Token ?? string.Empty);
        }

        public static bool operator !=(AssemblySerializationInfo info1, AssemblySerializationInfo info2)
            => !(info1 == info2);

        public override string ToString()
        {
            var sb = new StringBuilder(Name);
            if (Version != null && (Version.Major != 0 || Version.Minor != 0 || Version.Build != 0 || Version.Revision != 0))
                sb.Append(", Version=").Append(Version.ToString());
            if (Token != null && Token.Length > 0)
                sb.Append(", PublicKeyToken=").Append(Token);
            return sb.ToString();
        }
    }
}
