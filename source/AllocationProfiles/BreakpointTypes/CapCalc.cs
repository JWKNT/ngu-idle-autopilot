using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

/*
FILE PURPOSE

CapCalc is a small numeric helper for translating power-per-tick, target progress, and offsets
into safe integer resource caps. It is shared by breakpoint implementations and performs no game
mutation. Preserve overflow handling and rounding because one-unit errors change native tick
breakpoints at large resource scales.
*/
namespace NGUInjector.AllocationProfiles.BreakpointTypes
{
    internal class CapCalc
    {
        internal double PPT { get; set; }
        internal long Num { get; set; }

        internal int GetOffset()
        {
            return (int)Math.Floor(PPT * 50 * 10);
        }
    }
}
