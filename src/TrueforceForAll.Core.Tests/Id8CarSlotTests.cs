// CarID to per-car leaderboard slot.
//
// A CarID is (maker << 8) | member, and the leaderboard page is that flattened by prefix-summing
// the per-maker counts. Measured on the cabinet: markers written to all 50 slots came back with
// the Trueno 2door reading 0:51.900, which is slot 9.
//
// Worth pinning because a wrong mapping here is invisible. Every per-car board would still be full
// of plausible times, just attributed to the wrong cars, and the only way anyone would notice is
// wondering why their AE86 time was on the MR-S board.

using System.Collections.Generic;
using System.Linq;
using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    public class Id8CarSlotTests
    {
        /// <summary>The measurement that settled it. CarID 9 is maker 0 member 9, so slot 9, and
        /// the probe wrote 51000 + 9*100 = 51900 there.</summary>
        [Fact]
        public void TruenoTwoDoorIsSlotNine()
        {
            Assert.Equal(9, Id8CarTable.SlotForCarId(9));
        }

        /// <summary>The competing map in the exe at 0x00ff8248 disagrees on exactly these four.
        /// Had it won, two cars would be swapped and two flung to the end of the list.</summary>
        [Theory]
        [InlineData(4, 4)]     // Altezza: gear map says 5
        [InlineData(5, 5)]     // MR-S:    gear map says 4
        [InlineData(9, 9)]     // Trueno 2door: gear map says 38
        [InlineData(10, 10)]   // Celica:  gear map says 45
        public void TheFourCarsTheGearMapDisagreesOnUseTheLeaderboardMapping(int carId, int slot)
        {
            Assert.Equal(slot, Id8CarTable.SlotForCarId(carId));
        }

        /// <summary>Maker boundaries, which is where a prefix sum goes wrong if it goes wrong.
        /// Counts are 11, 9, 5, 6, 4, 7, 1, 1, 6.</summary>
        [Theory]
        [InlineData(0, 0)]        // maker 0, first
        [InlineData(10, 10)]      // maker 0, last
        [InlineData(256, 11)]     // maker 1, first
        [InlineData(264, 19)]     // maker 1, last
        [InlineData(512, 20)]     // maker 2, first
        [InlineData(516, 24)]     // maker 2, last: NSX, which ended page A on screen
        [InlineData(768, 25)]     // maker 3, first
        [InlineData(1536, 42)]    // maker 6, its only car
        [InlineData(1792, 43)]    // maker 7, its only car
        [InlineData(2048, 44)]    // maker 8, first
        [InlineData(2053, 49)]    // maker 8, last
        public void MakerBoundariesLandWhereThePrefixSumSaysTheyShould(int carId, int slot)
        {
            Assert.Equal(slot, Id8CarTable.SlotForCarId(carId));
        }

        /// <summary>Fifty cars onto fifty slots, once each. A collision would silently overwrite
        /// one car's board with another's.</summary>
        [Fact]
        public void EveryCarGetsItsOwnSlotAndTheyCoverZeroToFortyNine()
        {
            var slots = Id8CarTable.CarIdsInSlotOrder().Select(Id8CarTable.SlotForCarId).ToList();
            Assert.Equal(50, slots.Count);
            Assert.Equal(Enumerable.Range(0, 50), slots);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(11)]      // a gap inside maker 0's range
        [InlineData(9999)]
        public void AnUnknownCarIdIsRefusedRatherThanMappedToZero(int carId)
        {
            Assert.Equal(-1, Id8CarTable.SlotForCarId(carId));
        }

        /// <summary>Slot order is CarID order. The ranking screen displays cars in some other
        /// order, which is a display concern and deliberately not modelled here.</summary>
        [Fact]
        public void SlotOrderIsCarIdOrder()
        {
            var ids = Id8CarTable.CarIdsInSlotOrder().ToList();
            Assert.Equal(ids.OrderBy(i => i).ToList(), ids);
        }
    }
}
