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
        Assert.Equal("A Book.m4b", Assert.Single(book.GetProperty("files").EnumerateArray()).GetString());
        Assert.Equal("A Book — An Author", book.GetProperty("title").GetString());
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

    private static void WriteManifest(TempWorkspace workspace, params (string Id, string Folder, string[] Files)[] books)
    {
        var file = new
        {
            format = "openaudible-organizer-manifest",
            version = 1,
            books = books.ToDictionary(book => book.Id, book => new { folder = book.Folder, files = book.Files, title = book.Id })
        };
        File.WriteAllText(Path.Combine(workspace.Destination, LibraryManifest.FileName), JsonSerializer.Serialize(file));
    }
}
