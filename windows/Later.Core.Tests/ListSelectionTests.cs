using Xunit;

namespace Later.Core.Tests;

/// Only the selection's `Keep` rules are ported: clicks and arrow keys belong to the Windows list control.
public class ListSelectionTests
{
    readonly List<Guid> rows = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();

    [Fact]
    public void ARemovedSelectionMovesToTheNextPage()
    {
        var after = rows.Where(r => r != rows[2]).ToList();
        var (selected, cursor) = SelectionKeep.Keep([rows[2]], rows[2], after, new HashSet<Guid> { rows[2] }, rows);
        Assert.Equal(new HashSet<Guid> { rows[3] }, selected);
        Assert.Equal(rows[3], cursor);

        // The last page gone: the one before it.
        (selected, cursor) = SelectionKeep.Keep([rows[4]], rows[4], rows.Take(4).ToList(), new HashSet<Guid> { rows[4] }, rows);
        Assert.Equal(new HashSet<Guid> { rows[3] }, selected);
        Assert.Equal(rows[3], cursor);

        // A page that wasn't selected changes nothing.
        (selected, cursor) = SelectionKeep.Keep([rows[0]], rows[0], rows.Take(4).ToList(), new HashSet<Guid> { rows[4] }, rows);
        Assert.Equal(new HashSet<Guid> { rows[0] }, selected);
        Assert.Equal(rows[0], cursor);
    }

    [Fact]
    public void NothingLeftAndNoRowsLeftSelectsNothing()
    {
        var (selected, cursor) = SelectionKeep.Keep([rows[0]], rows[0], [], new HashSet<Guid>(rows), rows);
        Assert.Empty(selected);
        Assert.Null(cursor);
    }

    /// B and D selected, the keyboard on D; D alone is marked Done from its row. The arrows carry on from B, not the top.
    [Fact]
    public void APartlyRemovedSelectionKeepsTheKeyboardOnWhatStays()
    {
        var after = rows.Where(r => r != rows[3]).ToList();
        var (selected, cursor) = SelectionKeep.Keep([rows[1], rows[3]], rows[3], after, new HashSet<Guid> { rows[3] }, rows);
        Assert.Equal(new HashSet<Guid> { rows[1] }, selected);
        Assert.Equal(rows[1], cursor);
    }
}
