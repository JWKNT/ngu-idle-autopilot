using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

/*
FILE PURPOSE

This file centralizes stable limits audited from the shipped game assembly. Consumers use them
for bounds checks, not strategy tuning. Update values only with the matching game version: stale
maxima omit content and excessive maxima can index beyond native arrays.
*/
namespace NGUInjector
{
    internal static class Consts
    {
        internal const int MAX_WISH_ID = 230;
        internal const int MAX_GEAR_ID = 514;
    }
}
