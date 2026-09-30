using System.Text;
using Ledger.Dashboards;

var command = args.Length == 1 ? args[0] : string.Empty;
if (command is not ("generate" or "check"))
{
    Console.Error.WriteLine("Usage: dotnet run --project Ledger.Dashboards -- generate | check");
    return 2;
}

var root = FindRepositoryRoot();
if (root is null)
{
    Console.Error.WriteLine("Could not locate Ledger.slnx above the working directory or the tool.");
    return 2;
}

var outputDirectory = Path.Combine(root, DashboardGenerator.OutputDirectory);
var files = new DashboardGenerator().Generate();
var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

if (command == "generate")
{
    Directory.CreateDirectory(outputDirectory);
    foreach (var (name, content) in files)
    {
        File.WriteAllText(Path.Combine(outputDirectory, name), content, encoding);
        Console.WriteLine($"Wrote {DashboardGenerator.OutputDirectory}/{name}");
    }

    return 0;
}

var stale = files
    .Where(file => !File.Exists(Path.Combine(outputDirectory, file.Key))
        || !File.ReadAllBytes(Path.Combine(outputDirectory, file.Key)).AsSpan().SequenceEqual(encoding.GetBytes(file.Value)))
    .Select(file => file.Key)
    .ToList();

if (stale.Count == 0)
{
    Console.WriteLine("Generated dashboards are up to date.");
    return 0;
}

foreach (var name in stale)
{
    Console.Error.WriteLine($"Stale or missing: {DashboardGenerator.OutputDirectory}/{name}");
}

Console.Error.WriteLine("Run: dotnet run --project Ledger.Dashboards -- generate");
return 1;

static string? FindRepositoryRoot()
{
    foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        var directory = new DirectoryInfo(start);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Ledger.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }
    }

    return null;
}
