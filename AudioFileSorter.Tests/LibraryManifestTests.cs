using System.Text.Json;
using AudioFileSorter.Model;

namespace AudioFileSorter.Tests;

public class LibraryManifestTests
{
    [Fact]
    public void A_destination_without_one_has_an_empty_record()
    {
        using var workspace = new TempWorkspace();

        var manifest = LibraryManifest.Load(workspace.Destination);

        Assert.False(manifest.Existed);
        Assert.Null(manifest.Problem);
        Assert.Empty(manifest.Books);
    }

    [Fact]
    public void What_is_saved_is_read_back()
    {
        using var workspace = new TempWorkspace();
        var folder = Path.Combine(workspace.Destination, "Dennis E. Taylor", "Bobiverse", "Book 1");
        workspace.WriteDestinationFile(Path.Combine(folder, "We Are Legion.m4b"), "audio");
        workspace.WriteDestinationFile(Path.Combine(folder, "We Are Legion.pdf"), "pdf");
        var saved = LibraryManifest.Load(workspace.Destination);
        var entry = new ManifestEntry(folder, ["We Are Legion.m4b", "We Are Legion.pdf"], "We Are Legion (We Are Bob) — Dennis E. Taylor");

        Assert.True(saved.Set("b01", entry));
        saved.Save();
        var loaded = LibraryManifest.Load(workspace.Destination);

        Assert.True(loaded.Existed);
        Assert.Null(loaded.Problem);
        var read = Assert.Single(loaded.Books);
        Assert.Equal("b01", read.Key);
        Assert.Equal(entry.Folder, read.Value.Folder);
        Assert.Equal(entry.Files, read.Value.Files);
        Assert.Equal(entry.Title, read.Value.Title);
    }

    [Fact]
    public void The_file_records_paths_relative_to_the_destination_with_forward_slashes()
    {
        using var workspace = new TempWorkspace();
        var folder = Path.Combine(workspace.Destination, "An Author", "Saga", "Book 1");
        workspace.WriteDestinationFile(Path.Combine(folder, "A Book.m4b"), "audio");
        var manifest = LibraryManifest.Load(workspace.Destination);
        manifest.Set("b01", new ManifestEntry(folder, ["A Book.m4b"], "A Book — An Author"));

        manifest.Save();

        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(workspace.Destination, LibraryManifest.FileName)));
        var root = document.RootElement;
        Assert.Equal("openaudible-organizer-manifest", root.GetProperty("format").GetString());
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        // For whoever finds the file: deleting it is not free.
        var note = root.GetProperty("note").GetString();
        Assert.Contains("never given to another", note);
        Assert.Contains("automatic sorts pause", note);
        var book = root.GetProperty("books").GetProperty("b01");
        Assert.Equal("An Author/Saga/Book 1", book.GetProperty("folder").GetString());
        var file = Assert.Single(book.GetProperty("files").EnumerateArray());
        Assert.Equal("A Book.m4b", file.GetProperty("name").GetString());
        Assert.Equal("A Book — An Author", book.GetProperty("title").GetString());

        // What the file held when it went on record, so a later sort can tell it is still the one recorded.
        Assert.Equal(5, file.GetProperty("size").GetInt64());
        Assert.Matches("^[0-9a-f]{64}$", file.GetProperty("sample").GetString());
    }

    [Fact]
    public void A_recorded_file_that_holds_something_else_now_is_forgotten()
    {
        // As a sort whose record was lost leaves it: another book's file has been written under the name
        // the record still gives. The name alone must not make it this book's.
        using var workspace = new TempWorkspace();
        var folder = Path.Combine(workspace.Destination, "An Author", "Foo");
        workspace.WriteDestinationFile(Path.Combine(folder, "Foo.m4b"), "book-4-audio");
        workspace.WriteDestinationFile(Path.Combine(folder, "Foo.pdf"), "book-4-pdf");
        workspace.WriteDestinationFile(Path.Combine("An Author", "Bar", "Bar.m4b"), "book-5-audio");
        var manifest = LibraryManifest.Load(workspace.Destination);
        manifest.Set("b4", new ManifestEntry(folder, ["Foo.m4b", "Foo.pdf"], "Foo"));
        manifest.Set("b5", new ManifestEntry(Path.Combine(workspace.Destination, "An Author", "Bar"), ["Bar.m4b"], "Bar"));
        manifest.Save();

        workspace.WriteDestinationFile(Path.Combine(folder, "Foo.m4b"), "book-1-audio");
        workspace.WriteDestinationFile(Path.Combine("An Author", "Bar", "Bar.m4b"), "book-1-other");
        var loaded = LibraryManifest.Load(workspace.Destination);

        Assert.Equal(["Foo.pdf"], loaded.Get("b4")?.Files);
        Assert.Null(loaded.Get("b5"));
    }

    [Fact]
    public void A_file_on_record_without_what_it_held_is_forgotten()
    {
        // A hand edit, or a record an earlier build wrote: a name alone proves nothing.
        using var workspace = new TempWorkspace();
        workspace.WriteDestinationFile(Path.Combine("An Author", "A Book", "A Book.m4b"), "audio");
        var file = new
        {
            format = "openaudible-organizer-manifest",
            version = 1,
            books = new { b01 = new { folder = "An Author/A Book", files = new[] { "A Book.m4b" }, title = "A Book" } }
        };
        File.WriteAllText(Path.Combine(workspace.Destination, LibraryManifest.FileName), JsonSerializer.Serialize(file));

        var manifest = LibraryManifest.Load(workspace.Destination);

        Assert.Empty(manifest.Books);
        Assert.Null(manifest.Problem);
    }

    [Fact]
    public void A_file_is_the_books_only_while_it_holds_what_it_held_when_it_went_on_record()
    {
        using var workspace = new TempWorkspace();
        var folder = Path.Combine(workspace.Destination, "An Author", "A Book");
        var path = workspace.WriteDestinationFile(Path.Combine(folder, "A Book.m4b"), "audio");
        var other = workspace.WriteDestinationFile(Path.Combine(folder, "Other.m4b"), "audio");
        var manifest = LibraryManifest.Load(workspace.Destination);
        manifest.Set("b01", new ManifestEntry(folder, ["A Book.m4b"], "A Book"));

        Assert.True(manifest.IsRecorded("b01", path));
        Assert.False(manifest.IsRecorded("b02", path));
        Assert.False(manifest.IsRecorded("b01", other));

        // Checked when asked, not only when the record was read.
        File.WriteAllText(path, "other-book");
        Assert.False(manifest.IsRecorded("b01", path));
        File.WriteAllText(path, "audio");
        Assert.True(manifest.IsRecorded("b01", path));
    }

    [Fact]
    public void Setting_an_entry_read_from_the_record_keeps_what_its_files_held_then()
    {
        // Were what a file holds now taken for a file carried over from the old record, whatever had
        // been written under its name since would become the book's.
        using var workspace = new TempWorkspace();
        var folder = Path.Combine(workspace.Destination, "An Author", "A Book");
        var path = workspace.WriteDestinationFile(Path.Combine(folder, "A Book.m4b"), "audio");
        var first = LibraryManifest.Load(workspace.Destination);
        first.Set("b01", new ManifestEntry(folder, ["A Book.m4b"], "A Book"));
        first.Save();

        var manifest = LibraryManifest.Load(workspace.Destination);
        var entry = manifest.Get("b01")!;
        File.WriteAllText(path, "other-book");
        manifest.Set("b01", entry with { Title = "Renamed" });

        Assert.False(manifest.IsRecorded("b01", path));
        manifest.Save();
        Assert.Null(LibraryManifest.Load(workspace.Destination).Get("b01"));
    }

    [Fact]
    public void A_recorded_file_that_cannot_be_read_right_now_is_kept_but_proves_nothing_until_it_can()
    {
        // As an unreadable folder is: it keeps its folder from other books, and is taken for the book's once it can be checked.
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return;
        }

        using var workspace = new TempWorkspace();
        var folder = Path.Combine(workspace.Destination, "An Author", "A Book");
        var path = workspace.WriteDestinationFile(Path.Combine(folder, "A Book.m4b"), "audio");
        var first = LibraryManifest.Load(workspace.Destination);
        first.Set("b01", new ManifestEntry(folder, ["A Book.m4b"], "A Book"));
        first.Save();

        File.SetUnixFileMode(path, UnixFileMode.None);
        try
        {
            var manifest = LibraryManifest.Load(workspace.Destination);
            Assert.Equal(["A Book.m4b"], manifest.Get("b01")?.Files);
            Assert.False(manifest.IsRecorded("b01", path));

            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Assert.True(manifest.IsRecorded("b01", path));
        }
        finally
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void Saving_leaves_no_temporary_file_behind()
    {
        using var workspace = new TempWorkspace();

        LibraryManifest.Load(workspace.Destination).Save();

        Assert.Equal([LibraryManifest.FileName], Directory.GetFiles(workspace.Destination).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("OpenAudible Book Organizer sorts into this folder. Automatic sorts look for this file.\n")]
    [InlineData("{ \"format\": \"openaudible-organizer-manifest\", \"version\": 1, \"books\": { ")]
    [InlineData("{ \"format\": \"openaudible-organizer-manifest\", \"version\": 2, \"books\": {} }")]
    [InlineData("{ \"format\": \"something-else\", \"version\": 1, \"books\": {} }")]
    [InlineData("[]")]
    public void A_file_that_is_not_a_readable_manifest_is_rebuilt_with_a_warning(string content)
    {
        // The first is the plain marker the organizer used to leave before it kept a manifest.
        using var workspace = new TempWorkspace();
        File.WriteAllText(Path.Combine(workspace.Destination, LibraryManifest.FileName), content);

        var manifest = LibraryManifest.Load(workspace.Destination);

        Assert.True(manifest.Existed);
        Assert.Empty(manifest.Books);
        Assert.Contains("rebuilt", manifest.Problem);

        // No released version wrote this file, so a damaged one is never "expected": what it cost is said instead.
        Assert.DoesNotContain("expected", manifest.Problem);
        Assert.Contains("books that have left the export are no longer set aside", manifest.Problem);
    }

    [Fact]
    public void A_file_that_cannot_be_read_right_now_is_not_taken_for_an_empty_record()
    {
        // As when a virus scanner or a backup tool has it open: it may be a good record, which a
        // rebuilt one saved over it would lose.
        using var workspace = new TempWorkspace();
        LibraryManifest.Load(workspace.Destination).Save();
        var path = Path.Combine(workspace.Destination, LibraryManifest.FileName);

        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var error = Assert.Throws<IOException>(() => LibraryManifest.Load(workspace.Destination));

        Assert.Contains(LibraryManifest.FileName, error.Message);
        Assert.Contains("nothing was sorted", error.Message);
    }

    [Fact]
    public void A_book_whose_folder_cannot_be_looked_into_right_now_is_kept()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return;
        }

        using var workspace = new TempWorkspace();
        var folder = Path.Combine(workspace.Destination, "An Author", "A Book");
        workspace.WriteDestinationFile(Path.Combine(folder, "A Book.m4b"), "audio");
        WriteManifest(workspace, ("b01", "An Author/A Book", ["A Book.m4b"]));

        File.SetUnixFileMode(folder, UnixFileMode.None);
        try
        {
            var entry = Assert.Single(LibraryManifest.Load(workspace.Destination).Books);
            Assert.Equal(["A Book.m4b"], entry.Value.Files);
        }
        finally
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Theory]
    [InlineData(UnixFileMode.UserRead)]
    [InlineData(UnixFileMode.None)]
    public void A_book_whose_parent_folder_cannot_be_looked_into_right_now_is_kept(UnixFileMode parentMode)
    {
        // As a NAS permission change or a network hiccup leaves it: the book's folder cannot be told
        // from a deleted one, and dropping it would let another book be given its folder, and its copy.
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return;
        }

        using var workspace = new TempWorkspace();
        var author = Path.Combine(workspace.Destination, "An Author");
        workspace.WriteDestinationFile(Path.Combine(author, "A Book", "A Book.m4b"), "audio");
        WriteManifest(workspace, ("b01", "An Author/A Book", ["A Book.m4b"]));

        File.SetUnixFileMode(author, parentMode);
        try
        {
            var entry = Assert.Single(LibraryManifest.Load(workspace.Destination).Books);
            Assert.Equal(["A Book.m4b"], entry.Value.Files);
        }
        finally
        {
            File.SetUnixFileMode(author, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void A_folder_spelled_with_characters_the_system_allows_is_recorded()
    {
        // ':' and '\' separate nothing on Linux and macOS, where a folder spelled with them is a book like any other.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TempWorkspace();
        var folder = Path.Combine(workspace.Destination, "An Author", "Foo: Bar \\ Baz");
        workspace.WriteDestinationFile(Path.Combine(folder, "Foo: Bar.m4b"), "audio");
        var manifest = LibraryManifest.Load(workspace.Destination);

        Assert.True(manifest.Set("b01", new ManifestEntry(folder, ["Foo: Bar.m4b"], "Foo: Bar")));
        manifest.Save();

        var entry = Assert.Single(LibraryManifest.Load(workspace.Destination).Books).Value;
        Assert.Equal((folder, "Foo: Bar.m4b"), (entry.Folder, Assert.Single(entry.Files)));
    }

    [Fact]
    public void Saving_what_is_already_there_leaves_the_file_alone()
    {
        using var workspace = new TempWorkspace();
        var folder = Path.Combine(workspace.Destination, "An Author", "A Book");
        workspace.WriteDestinationFile(Path.Combine(folder, "A Book.m4b"), "audio");
        var manifest = LibraryManifest.Load(workspace.Destination);
        manifest.Set("b01", new ManifestEntry(folder, ["A Book.m4b"], "A Book"));
        manifest.Save();
        var path = Path.Combine(workspace.Destination, LibraryManifest.FileName);
        var written = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, written);

        LibraryManifest.Load(workspace.Destination).Save();
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));

        manifest.Remove("b01");
        manifest.Save();
        Assert.NotEqual(written, File.GetLastWriteTimeUtc(path));
    }

    [Theory]
    [InlineData("../Outside")]
    [InlineData("An Author/../../Outside")]
    [InlineData("/etc")]
    [InlineData("C:/Windows")]
    [InlineData("An Author\\A Book")]
    [InlineData("An Author//A Book")]
    [InlineData("./An Author/A Book")]
    [InlineData("An Author/A Book\0")]
    public void An_entry_whose_folder_is_not_a_plain_path_inside_the_destination_is_ignored(string folder)
    {
        using var workspace = new TempWorkspace();
        Directory.CreateDirectory(Path.Combine(workspace.Root, "Outside"));
        File.WriteAllText(Path.Combine(workspace.Root, "Outside", "A Book.m4b"), "audio");
        workspace.WriteDestinationFile(Path.Combine("An Author", "A Book", "A Book.m4b"), "audio");
        WriteManifest(workspace, ("b01", folder, ["A Book.m4b"]));

        var manifest = LibraryManifest.Load(workspace.Destination);

        Assert.Empty(manifest.Books);
        Assert.Null(manifest.Problem);
    }

    [Theory]
    [InlineData("../A Book.m4b")]
    [InlineData("..")]
    [InlineData("sub/A Book.m4b")]
    [InlineData("sub\\A Book.m4b")]
    [InlineData("A Book.m4b\0")]
    public void A_file_name_that_leads_out_of_the_books_folder_is_ignored(string file)
    {
        using var workspace = new TempWorkspace();
        workspace.WriteDestinationFile(Path.Combine("An Author", "A Book.m4b"), "audio");
        workspace.WriteDestinationFile(Path.Combine("An Author", "A Book", "sub", "A Book.m4b"), "audio");
        WriteManifest(workspace, ("b01", "An Author/A Book", [file]));

        Assert.Empty(LibraryManifest.Load(workspace.Destination).Books);
    }

    [Fact]
    public void A_book_whose_folder_or_files_are_gone_is_forgotten()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteDestinationFile(Path.Combine("An Author", "Kept", "Kept.m4b"), "audio");
        Directory.CreateDirectory(Path.Combine(workspace.Destination, "An Author", "Emptied"));
        WriteManifest(
            workspace,
            ("kept", "An Author/Kept", ["Kept.m4b", "Kept.pdf"]),
            ("emptied", "An Author/Emptied", ["Emptied.m4b"]),
            ("deleted", "An Author/Deleted", ["Deleted.m4b"]));

        var manifest = LibraryManifest.Load(workspace.Destination);

        // Only the files still there are kept for a book that is still there.
        var entry = Assert.Single(manifest.Books);
        Assert.Equal("kept", entry.Key);
        Assert.Equal(["Kept.m4b"], entry.Value.Files);
    }

    [Fact]
    public void A_folder_recorded_for_two_books_stays_its_first_owners()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteDestinationFile(Path.Combine("An Author", "Foo", "Foo.m4b"), "audio");
        WriteManifest(workspace, ("first", "An Author/Foo", ["Foo.m4b"]), ("second", "An Author/Foo", ["Foo.m4b"]));

        Assert.Equal("first", Assert.Single(LibraryManifest.Load(workspace.Destination).Books).Key);
    }

    [Fact]
    public void Set_refuses_an_entry_outside_the_destination()
    {
        using var workspace = new TempWorkspace();
        var manifest = LibraryManifest.Load(workspace.Destination);

        Assert.False(manifest.Set("outside", new ManifestEntry(workspace.Source, ["a-book.m4b"], "A Book")));
        Assert.False(manifest.Set("root", new ManifestEntry(workspace.Destination, ["a-book.m4b"], "A Book")));
        Assert.False(manifest.Set("climbing", new ManifestEntry(Path.Combine(workspace.Destination, "Author"), ["../a-book.m4b"], "A Book")));
        Assert.Empty(manifest.Books);
    }

    [Fact]
    public void Remove_forgets_a_book()
    {
        using var workspace = new TempWorkspace();
        var folder = Path.Combine(workspace.Destination, "An Author", "A Book");
        var manifest = LibraryManifest.Load(workspace.Destination);
        manifest.Set("b01", new ManifestEntry(folder, ["A Book.m4b"], "A Book"));

        Assert.True(manifest.Remove("b01"));
        Assert.Null(manifest.Get("b01"));
    }

    /// <summary>Writes a record by hand, each file on it with what it holds now, as a sort records a file (just the name when there is none).</summary>
    private static void WriteManifest(TempWorkspace workspace, params (string Id, string Folder, string[] Files)[] books)
    {
        var file = new
        {
            format = "openaudible-organizer-manifest",
            version = 1,
            books = books.ToDictionary(book => book.Id, book => new
            {
                folder = book.Folder,
                files = book.Files.Select(name => FileComparison.Stamp(Path.Combine(workspace.Destination, book.Folder, name)) is { } stamp
                    ? (object)new { name, size = stamp.Size, sample = stamp.Sample }
                    : new { name }),
                title = book.Id
            })
        };
        File.WriteAllText(Path.Combine(workspace.Destination, LibraryManifest.FileName), JsonSerializer.Serialize(file));
    }
}
