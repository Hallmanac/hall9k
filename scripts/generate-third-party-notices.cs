// Generates THIRD-PARTY-NOTICES.txt for a Hall9k release payload.
//
//   dotnet run scripts/generate-third-party-notices.cs -- <publish-dir> [--output <file>]
//       [--packages <nuget-cache>] [--license-texts <dir>]
//
// <publish-dir> is a self-contained publish output holding both h9k.deps.json and h9kd.deps.json
// (release.yml publishes the two into one directory). Every package library and runtime pack those
// files list is looked up in the restored NuGet cache (--packages, else NUGET_PACKAGES, else
// ~/.nuget/packages) and read from its own .nuspec: the SPDX license expression, the copyright line
// the package declares, and the package's own license file when it ships one (named by a
// license type="file" nuspec, else a LICENSE-shaped file at the package root or inside a root-level
// LICENSE folder). A package without its own license file gets the standard text for its SPDX id
// from <license-texts>/<id>.txt, whose {copyright} first line is replaced by the declared copyright,
// or by "Copyright (c) <authors>" from the nuspec when it declares none. A license that requires
// the notice to travel (every standard text here except 0BSD) with neither is an error, so a
// redistributed license never refers to a copyright notice that is not there.
// Every THIRD-PARTY-NOTICES file found inside a package or runtime pack is reproduced once, with the
// packages that ship it.
//
// The script exits 1, writing nothing, when any listed package's license cannot be determined: no
// restored copy in the cache, neither an SPDX expression nor a license file in the nuspec, an SPDX id
// with neither a license file in the package nor a standard text here, or an attribution-requiring
// standard text with no copyright and no authors to put on it. A new license family shows up as a failed release
// rather than a silently incomplete notice, and the fix is to add scripts/license-texts/<id>.txt.
//
// Restore first (dotnet publish does): the cache must hold the exact versions the deps.json files name.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

string? publishDirectory = null;
string? outputPath = null;
string? packagesRoot = null;
string? textsRoot = null;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--output" when i + 1 < args.Length:
            outputPath = args[++i];
            break;
        case "--packages" when i + 1 < args.Length:
            packagesRoot = args[++i];
            break;
        case "--license-texts" when i + 1 < args.Length:
            textsRoot = args[++i];
            break;
        case var option when option.StartsWith("--", StringComparison.Ordinal):
            return Fail($"Unknown or incomplete option '{option}'. See the usage at the top of this script.");
        case var positional when publishDirectory is null:
            publishDirectory = positional;
            break;
        default:
            return Fail($"Unexpected argument '{args[i]}'. See the usage at the top of this script.");
    }
}

if (publishDirectory is null || !Directory.Exists(publishDirectory))
{
    return Fail("Pass the publish output directory (the one holding h9k.deps.json and h9kd.deps.json) as the first argument.");
}

string entryPointDirectory = AppContext.GetData("EntryPointFilePath") is string entryPoint
    ? Path.GetDirectoryName(Path.GetFullPath(entryPoint)) ?? Directory.GetCurrentDirectory()
    : Path.Combine(Directory.GetCurrentDirectory(), "scripts");

textsRoot ??= Path.Combine(entryPointDirectory, "license-texts");
packagesRoot ??= Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } fromEnvironment
    ? fromEnvironment
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
outputPath ??= Path.Combine(publishDirectory, "THIRD-PARTY-NOTICES.txt");

string[] dependencyFiles = ["h9k.deps.json", "h9kd.deps.json"];
List<string> problems = [];

if (!Directory.Exists(packagesRoot))
{
    return Fail($"The NuGet package cache '{packagesRoot}' does not exist; restore first or pass --packages.");
}

if (!Directory.Exists(textsRoot))
{
    return Fail($"The standard license text directory '{textsRoot}' does not exist; pass --license-texts.");
}

// name -> version pairs; a runtime pack's deps.json id carries a "runtimepack." prefix naming the
// restored Microsoft.*.App.Runtime.<rid> package, which is where its license and notices live.
SortedDictionary<string, PackageRef> libraries = new(StringComparer.OrdinalIgnoreCase);
foreach (string dependencyFile in dependencyFiles)
{
    string path = Path.Combine(publishDirectory, dependencyFile);
    if (!File.Exists(path))
    {
        problems.Add($"{dependencyFile} is missing from '{publishDirectory}'.");
        continue;
    }

    using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
    if (!document.RootElement.TryGetProperty("libraries", out JsonElement libraryEntries))
    {
        problems.Add($"{dependencyFile} has no libraries section.");
        continue;
    }

    foreach (JsonProperty entry in libraryEntries.EnumerateObject())
    {
        string type = entry.Value.TryGetProperty("type", out JsonElement typeElement)
            ? typeElement.GetString() ?? string.Empty
            : string.Empty;
        if (type is not ("package" or "runtimepack"))
        {
            continue;
        }

        int slash = entry.Name.LastIndexOf('/');
        string name = entry.Name[..slash];
        string version = entry.Name[(slash + 1)..];
        bool isRuntimePack = type == "runtimepack";
        if (isRuntimePack)
        {
            name = name.Replace("runtimepack.", string.Empty, StringComparison.Ordinal);
        }

        libraries[$"{name}/{version}"] = new PackageRef(name, version, isRuntimePack);
    }
}

Regex licenseFileName = new(@"^(LICENSE|LICENCE|COPYING)(\.(txt|md))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
Regex noticesFileName = new(@"^THIRD[-_ ]?PARTY[-_ ]?NOTICES?(\.(txt|md))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
Regex spdxToken = new(@"[A-Za-z0-9.+\-]+", RegexOptions.CultureInvariant);

// Standard texts that do not require a copyright notice to be kept; every other one here does.
HashSet<string> attributionFreeLicenses = new(["0BSD"], StringComparer.Ordinal);

List<Entry> entries = [];
foreach (PackageRef package in libraries.Values)
{
    string label = $"{package.Name} {package.Version}";
    string directory = Path.Combine(packagesRoot, package.Name.ToLowerInvariant(), package.Version.ToLowerInvariant());
    if (!Directory.Exists(directory))
    {
        problems.Add($"{label}: not in the NuGet cache at '{directory}'.");
        continue;
    }

    string? nuspecPath = Directory.EnumerateFiles(directory, "*.nuspec", SearchOption.TopDirectoryOnly).FirstOrDefault();
    if (nuspecPath is null)
    {
        problems.Add($"{label}: no .nuspec in '{directory}'.");
        continue;
    }

    XElement? metadata = XDocument.Load(nuspecPath).Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata");
    string? Field(string field) => metadata?.Elements().FirstOrDefault(e => e.Name.LocalName == field)?.Value.Trim();
    XElement? licenseElement = metadata?.Elements().FirstOrDefault(e => e.Name.LocalName == "license");

    string? licenseType = licenseElement?.Attribute("type")?.Value;
    string? expression = licenseType == "expression" ? licenseElement?.Value.Trim() : null;
    string? declaredLicenseFile = licenseType == "file" && licenseElement?.Value.Trim() is { Length: > 0 } fileName
        ? Path.Combine(directory, fileName.Replace('\\', '/'))
        : null;

    if (string.IsNullOrWhiteSpace(expression))
    {
        if (declaredLicenseFile is null || !File.Exists(declaredLicenseFile))
        {
            problems.Add($"{label}: its nuspec declares neither an SPDX license expression nor an existing license file (license element: {licenseElement?.ToString() ?? "absent"}, licenseUrl: {Field("licenseUrl") ?? "absent"}).");
            continue;
        }

        expression = $"license file {Path.GetFileName(declaredLicenseFile)} (no SPDX expression)";
    }

    string? copyright = Field("copyright") is { Length: > 0 } declared ? declared : null;
    string? authors = Field("authors") is { Length: > 0 } declaredAuthors ? Regex.Replace(declaredAuthors, @"\s*[,;]\s*", ", ") : null;

    // The package's own license file wins over a standard text; a nuspec that points at one
    // (license type="file") names it, otherwise a LICENSE-shaped file at the package root, or inside
    // a root-level LICENSE folder, counts.
    string? ownLicensePath = declaredLicenseFile ?? FindOwnLicense(directory, licenseFileName);

    string text;
    string source;
    string copyrightDisplay = copyright ?? "none declared by the package";
    if (ownLicensePath is not null && File.Exists(ownLicensePath))
    {
        text = Normalize(File.ReadAllText(ownLicensePath));
        source = $"the package's own {Path.GetRelativePath(directory, ownLicensePath).Replace('\\', '/')}";
    }
    else
    {
        List<string> identifiers = [.. spdxToken.Matches(expression)
            .Select(m => m.Value)
            .Where(v => v is not ("AND" or "OR" or "WITH"))
            .Distinct(StringComparer.Ordinal)];
        List<string> missing = [.. identifiers.Where(id => !File.Exists(Path.Combine(textsRoot, $"{id}.txt")))];
        if (missing.Count > 0)
        {
            problems.Add($"{label}: license '{expression}' has no standard text for {string.Join(", ", missing)}; add scripts/license-texts/<id>.txt.");
            continue;
        }

        // Only the copyright the package declares is a copyright; its authors are the next best
        // attribution, and the text says so in the Copyright line of the entry.
        string? noticeLine = copyright ?? (authors is null ? null : $"Copyright (c) {authors}");
        if (noticeLine is null && identifiers.Any(id => !attributionFreeLicenses.Contains(id)))
        {
            problems.Add($"{label}: license '{expression}' requires its copyright notice to be kept, but the nuspec declares neither a copyright nor authors to put on it.");
            continue;
        }

        copyrightDisplay = copyright
            ?? (authors is null ? copyrightDisplay : $"none declared by the package; the license text carries its authors instead: {noticeLine}");
        text = string.Join(
            "\n\n",
            identifiers.Select(id => ApplyCopyright(Normalize(File.ReadAllText(Path.Combine(textsRoot, $"{id}.txt"))), noticeLine)));
        source = $"the standard text for {string.Join(", ", identifiers)}";
    }

    // Recursive, so a notices file in a subfolder of the package is not missed.
    List<NoticesFile> notices = [.. Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
        .Where(f => noticesFileName.IsMatch(Path.GetFileName(f)))
        .Order(StringComparer.Ordinal)
        .Select(f => new NoticesFile(Path.GetRelativePath(directory, f).Replace('\\', '/'), Normalize(File.ReadAllText(f))))];

    entries.Add(new Entry(package, expression, copyrightDisplay, source, text, notices));
}

if (problems.Count > 0)
{
    Console.Error.WriteLine("Cannot determine every package's license; no notices file written:");
    foreach (string problem in problems)
    {
        Console.Error.WriteLine($"  - {problem}");
    }

    return 1;
}

StringBuilder output = new();
output.Append("THIRD-PARTY NOTICES\n\n");
output.Append("Hall9k itself is licensed under the Apache License, Version 2.0 (see LICENSE and NOTICE).\n");
output.Append("This release archive also redistributes the .NET runtime and the NuGet packages listed\n");
output.Append("below, each under its own license. The full license text for every entry follows the\n");
output.Append("summary, and the notices files shipped inside those packages and runtime packs follow that.\n");
output.Append("This file is generated at release time by scripts/generate-third-party-notices.cs.\n\n");

output.Append("SUMMARY\n=======\n\n");
foreach (Entry entry in entries)
{
    output.Append($"{entry.Display}  |  {entry.Package.Version}  |  {entry.Expression}\n");
}

output.Append("\n\nLICENSES\n========\n");
foreach (Entry entry in entries)
{
    output.Append("\n----------------------------------------------------------------------\n");
    output.Append($"{entry.Display} {entry.Package.Version}\n");
    output.Append($"License: {entry.Expression}\n");
    output.Append($"Copyright: {entry.Copyright}\n");
    output.Append($"License text: {entry.Source}\n");
    output.Append("----------------------------------------------------------------------\n\n");
    output.Append(entry.Text.TrimEnd('\n'));
    output.Append('\n');
}

// Many packages ship byte-identical notices; reproduce each distinct text once with its owners.
List<IGrouping<string, (Entry Entry, NoticesFile File)>> noticeGroups = [.. entries
    .SelectMany(e => e.Notices.Select(n => (Entry: e, File: n)))
    .GroupBy(x => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(x.File.Content))))];

output.Append("\n\nNOTICES SHIPPED INSIDE PACKAGES AND RUNTIME PACKS\n=================================================\n");
if (noticeGroups.Count == 0)
{
    output.Append("\n(none)\n");
}

foreach (IGrouping<string, (Entry Entry, NoticesFile File)> group in noticeGroups)
{
    output.Append("\n----------------------------------------------------------------------\n");
    output.Append("Shipped with:\n");
    foreach ((Entry entry, NoticesFile file) in group)
    {
        output.Append($"  {entry.Display} {entry.Package.Version} ({file.RelativePath})\n");
    }

    output.Append("----------------------------------------------------------------------\n\n");
    output.Append(group.First().File.Content.TrimEnd('\n'));
    output.Append('\n');
}

File.WriteAllText(outputPath, output.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
Console.WriteLine($"Wrote {outputPath}: {entries.Count} libraries, {noticeGroups.Count} distinct notices files.");
return 0;

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}

static string? FindOwnLicense(string directory, Regex licenseFileName)
{
    string? atRoot = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
        .Order(StringComparer.Ordinal)
        .FirstOrDefault(f => licenseFileName.IsMatch(Path.GetFileName(f)));
    return atRoot ?? Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly)
        .Where(d => licenseFileName.IsMatch(Path.GetFileName(d)))
        .SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.TopDirectoryOnly))
        .Order(StringComparer.Ordinal)
        .FirstOrDefault(f => licenseFileName.IsMatch(Path.GetFileName(f)));
}

static string Normalize(string content) => content.Replace("\r\n", "\n").Replace('\r', '\n');

static string ApplyCopyright(string template, string? copyright) => copyright is null
    ? template.Replace("{copyright}\n\n", string.Empty, StringComparison.Ordinal)
    : template.Replace("{copyright}", copyright, StringComparison.Ordinal);

internal sealed record PackageRef(string Name, string Version, bool IsRuntimePack);

internal sealed record NoticesFile(string RelativePath, string Content);

internal sealed record Entry(
    PackageRef Package,
    string Expression,
    string Copyright,
    string Source,
    string Text,
    List<NoticesFile> Notices)
{
    public string Display => Package.IsRuntimePack ? $"{Package.Name} (runtime pack)" : Package.Name;
}
