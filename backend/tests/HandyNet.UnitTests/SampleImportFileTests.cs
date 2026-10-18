using HandyNet.Modules.Imports.Application;
using HandyNet.Modules.Imports.Domain;

namespace HandyNet.UnitTests;

/// <summary>The demo files under samples/import must stay importable without manual mapping.</summary>
public class SampleImportFileTests
{
    [Theory]
    [InlineData("HandyNet_Import_Ersatzteile.csv", 30)]
    [InlineData("HandyNet_Import_Zubehoer.xlsx", 24)]
    public void Sample_files_parse_with_the_guessed_mapping_and_without_row_errors(string fileName, int expectedRows)
    {
        var path = Path.Combine(RepoRoot(), "samples", "import", fileName);
        var format = TabularReaders.DetectFormat(fileName);
        Assert.True(format.IsSuccess);

        var data = TabularReaders.Read(format.Value, File.ReadAllBytes(path));
        Assert.True(data.IsSuccess);

        var mapping = MappingGuesser.Guess(data.Value.Headers);
        Assert.Equal(ImportField.All.Order(), mapping.Keys.Order());

        var rows = data.Value.Rows.Select(r => RowParser.Parse(r.RowNumber, r.Values, mapping, new ParseDefaults())).ToList();
        Assert.Equal(expectedRows, rows.Count);
        Assert.All(rows, r => Assert.True(r.IsValid, $"row {r.RowNumber}: {string.Join("; ", r.Errors)}"));
        Assert.Equal(rows.Count, rows.Select(r => r.SkuKey).Distinct().Count());
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "samples")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Repository root with samples/ not found.");
    }
}
