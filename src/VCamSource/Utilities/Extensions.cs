using System;
using DirectN;

namespace VCamSource.Utilities
{
    internal static class Extensions
    {
        public static string GetMFName(this Guid guid) => typeof(MFConstants).GetGuidName(guid);
        public static string GetMFName(this KSIDENTIFIER id) => typeof(MFConstants).GetGuidName(id.__union_0.__field_0.Set) + " " + id.__union_0.__field_0.Id;
    }
}

