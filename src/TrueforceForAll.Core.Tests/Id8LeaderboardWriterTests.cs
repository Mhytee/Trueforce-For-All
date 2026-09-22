// Bounds tests for the leaderboard write addressing.
//
// These matter more than most: the four leaderboard tables sit CONSECUTIVELY in the game's memory
// (measured live, the gap between neighbouring bases is exactly one table's size). So an offset
// that runs one record past the end of its table does not fail, it lands in the next board and
// corrupts a row that looked untouched. Every case below is really asking "can this write escape
// its own table".

using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    public class Id8LeaderboardWriterTests
    {
        private const int Slots = 32;      // 16 courses x 2 directions, as the game reports
        private const int RecSize = 48;

        [Theory]
        [InlineData(Id8Board.OnlineTopTen, 10)]
        [InlineData(Id8Board.ShopTopTen, 10)]
        [InlineData(Id8Board.OnlinePerCar, 50)]
        [InlineData(Id8Board.ShopPerCar, 50)]
        public void TopTenBoardsPageByRankAndPerCarBoardsPageByCar(Id8Board board, int expected)
        {
            Assert.Equal(expected, Id8LeaderboardWriter.PagesFor(board));
        }

        /// <summary>The offset has to match the game's own arithmetic at 0x00a01640:
        /// record = 48 * (direction + 2*course + 32*page).</summary>
        [Theory]
        [InlineData(0, 0, 0, 0)]
        [InlineData(0, 1, 0, 1 * RecSize)]
        [InlineData(3, 0, 0, 6 * RecSize)]
        [InlineData(15, 1, 0, 31 * RecSize)]
        [InlineData(0, 0, 1, 32 * RecSize)]
        [InlineData(3, 0, 9, (6 + 32 * 9) * RecSize)]
        public void OffsetMatchesTheGamesOwnArithmetic(int course, int dir, int page, long expected)
        {
            Assert.True(Id8LeaderboardWriter.TryRecordOffset(
                Id8Board.OnlineTopTen, Slots, course, dir, page, out long off));
            Assert.Equal(expected, off);
        }

        [Fact]
        public void LastValidRecordFitsExactly()
        {
            // Rank 9, course 15, direction 1: the very last record of a top-ten board.
            Assert.True(Id8LeaderboardWriter.TryRecordOffset(
                Id8Board.OnlineTopTen, Slots, 15, 1, 9, out long off));
            Assert.Equal((long)RecSize * Slots * 10 - RecSize, off);
        }

        [Fact]
        public void PageBeyondTheBoardIsRefusedRatherThanClamped()
        {
            // Page 10 on a ten-rank board is the first record of the NEXT table in memory.
            Assert.False(Id8LeaderboardWriter.TryRecordOffset(
                Id8Board.OnlineTopTen, Slots, 0, 0, 10, out _));
            // A per-car board legitimately has 50 pages, so the same page number is fine there.
            Assert.True(Id8LeaderboardWriter.TryRecordOffset(
                Id8Board.OnlinePerCar, Slots, 0, 0, 10, out _));
            Assert.False(Id8LeaderboardWriter.TryRecordOffset(
                Id8Board.OnlinePerCar, Slots, 0, 0, 50, out _));
        }

        [Theory]
        [InlineData(-1, 0, 0)]
        [InlineData(16, 0, 0)]
        [InlineData(0, -1, 0)]
        [InlineData(0, 2, 0)]
        [InlineData(0, 0, -1)]
        public void OutOfRangeInputsAreRefused(int course, int dir, int page)
        {
            Assert.False(Id8LeaderboardWriter.TryRecordOffset(
                Id8Board.OnlineTopTen, Slots, course, dir, page, out _));
        }

        /// <summary>A nonsense slot count must not be trusted into an address. If the count read
        /// back wrong, every offset derived from it is wrong too.</summary>
        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(257)]
        public void UnusableSlotCountIsRefused(int slots)
        {
            Assert.False(Id8LeaderboardWriter.TryRecordOffset(
                Id8Board.OnlineTopTen, slots, 0, 0, 0, out _));
        }

        /// <summary>With a smaller table than the standard 32, the high courses must fall away
        /// rather than read off the end.</summary>
        [Fact]
        public void SlotBeyondTheReportedCountIsRefused()
        {
            Assert.True(Id8LeaderboardWriter.TryRecordOffset(Id8Board.OnlineTopTen, 8, 3, 1, 0, out _));
            Assert.False(Id8LeaderboardWriter.TryRecordOffset(Id8Board.OnlineTopTen, 8, 4, 0, 0, out _));
        }

        /// <summary>No (course, direction, page) inside one board may produce an offset that
        /// reaches the next table. This is the property the consecutive layout makes dangerous,
        /// so it is asserted exhaustively rather than sampled.</summary>
        [Theory]
        [InlineData(Id8Board.OnlineTopTen)]
        [InlineData(Id8Board.ShopTopTen)]
        [InlineData(Id8Board.OnlinePerCar)]
        [InlineData(Id8Board.ShopPerCar)]
        public void NoAcceptedOffsetEverEscapesItsOwnTable(Id8Board board)
        {
            long extent = (long)RecSize * Slots * Id8LeaderboardWriter.PagesFor(board);
            for (int page = 0; page < Id8LeaderboardWriter.PagesFor(board) + 2; page++)
                for (int course = 0; course <= 15; course++)
                    for (int dir = 0; dir <= 1; dir++)
                        if (Id8LeaderboardWriter.TryRecordOffset(board, Slots, course, dir, page, out long off))
                        {
                            Assert.True(off >= 0);
                            Assert.True(off + RecSize <= extent);
                        }
        }
    }
}
