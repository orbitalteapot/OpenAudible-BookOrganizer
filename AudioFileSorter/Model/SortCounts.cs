namespace AudioFileSorter.Model;

/// <summary>
/// How many books ended up in each state. The buckets are disjoint: every book lands in exactly
/// one, so they add up to the number of books processed and can be shown side by side without
/// anyone having to know that one number is "part of" another.
///
/// A book that qualifies for several is counted once, by the first match in this order:
/// Failed, NotFound, Updated, New, Moved, UpToDate. So a loose file from an older version that was
/// moved into its folder and then found to be out of date and replaced counts as Updated.
/// </summary>
/// <param name="New">Books written where nothing was before.</param>
/// <param name="Updated">Books whose out-of-date copy at the destination was replaced.</param>
/// <param name="Moved">
/// Books already in the destination but not where they now belong, moved there and otherwise
/// already up to date: left loose in an author or series folder by an older version, or filed
/// before their series details changed.
/// </param>
/// <param name="UpToDate">Books already present and current, so nothing was written.</param>
/// <param name="NotFound">
/// Books listed in the export with no file in the source folder, usually because they have not
/// been downloaded. Kept apart from <paramref name="UpToDate"/>: "not here" and "already
/// organised" mean very different things to whoever reads the number.
/// </param>
/// <param name="Failed">Books that could not be sorted because of an error.</param>
public sealed record SortCounts(int New, int Updated, int Moved, int UpToDate, int NotFound, int Failed)
{
    public static readonly SortCounts Empty = new(0, 0, 0, 0, 0, 0);

    public int Total => New + Updated + Moved + UpToDate + NotFound + Failed;
}
