namespace AudioFileSorter.Model;

public enum SortProblemKind
{
    /// <summary>The book is in the export but has no file in the source folder.</summary>
    NotFound,

    /// <summary>The book could not be sorted.</summary>
    Failed,

    /// <summary>The book was sorted, but something about it is worth knowing.</summary>
    Warning
}

/// <summary>Something that went wrong with one book, worded for the person running the sort.</summary>
/// <param name="Book">The book as a person would name it: "We Are Legion (We Are Bob) — Dennis E. Taylor".</param>
/// <param name="Message">What happened, in plain language: "No audio file for this book in the source folder".</param>
public sealed record SortProblem(SortProblemKind Kind, string Book, string Message);
