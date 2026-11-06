using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

if (args.Length != 3) throw new ArgumentException("Usage: ArtifactChecks assembly.dll symbols.pdb git-sha");
using var dll = File.OpenRead(args[0]);
using var pe = new PEReader(dll);
var metadata = pe.GetMetadataReader();
var entries = pe.ReadDebugDirectory();
if (!entries.Any(e => e.Type == DebugDirectoryEntryType.Reproducible))
    throw new Exception("PE has no deterministic build marker.");
var codeViews = entries.Where(e => e.Type == DebugDirectoryEntryType.CodeView).ToArray();
if (codeViews.Length != 1) throw new Exception("Expected exactly one CodeView entry.");
var codeView = pe.ReadCodeViewDebugDirectoryData(codeViews[0]);
if (!codeView.Path.StartsWith("/_/", StringComparison.Ordinal) || !codeView.Path.EndsWith(".pdb", StringComparison.Ordinal))
    throw new Exception("CodeView leaks an unmapped compiler path: " + codeView.Path);
using var pdbStream = File.OpenRead(args[1]);
using var provider = MetadataReaderProvider.FromPortablePdbStream(pdbStream);
var pdb = provider.GetMetadataReader();
if (new Guid(pdb.DebugMetadataHeader!.Id.AsSpan(0,16)) != codeView.Guid)
    throw new Exception("Portable PDB does not match the PE CodeView identity.");
var documents = pdb.Documents.Select(h => pdb.GetString(pdb.GetDocument(h).Name)).ToArray();
if (documents.Length == 0 || documents.Any(p => !p.StartsWith("/_/", StringComparison.Ordinal) || p.Contains('\\')))
    throw new Exception("Portable PDB contains unmapped documents: " + string.Join(", ", documents));
var sourceLinkCount = 0;
foreach (var handle in pdb.CustomDebugInformation)
{
    var record = pdb.GetCustomDebugInformation(handle);
    if (pdb.GetGuid(record.Kind) != new Guid("CC110556-A091-4D38-9FEC-25AB9A351A6A")) continue;
    using var sourceLink = System.Text.Json.JsonDocument.Parse(pdb.GetBlobBytes(record.Value));
    foreach (var mapping in sourceLink.RootElement.GetProperty("documents").EnumerateObject())
        if (!mapping.Name.StartsWith("/_/", StringComparison.Ordinal) ||
            !Uri.TryCreate(mapping.Value.GetString(), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
            throw new Exception("SourceLink contains an unmapped or local path.");
    sourceLinkCount++;
}

string? informationalVersion = null;
foreach (var h in metadata.GetAssemblyDefinition().GetCustomAttributes())
{
    var attribute = metadata.GetCustomAttribute(h);
    if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
    var constructor = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
    if (constructor.Parent.Kind != HandleKind.TypeReference) continue;
    var type = metadata.GetTypeReference((TypeReferenceHandle)constructor.Parent);
    if (metadata.GetString(type.Name) != "AssemblyInformationalVersionAttribute") continue;
    var blob = metadata.GetBlobReader(attribute.Value);
    if (blob.ReadUInt16() != 1) throw new Exception("Invalid assembly attribute.");
    informationalVersion = blob.ReadSerializedString();
}
if (informationalVersion is null || !informationalVersion.Contains(args[2], StringComparison.Ordinal))
    throw new Exception("InformationalVersion lost its Git SHA: " + informationalVersion);
Console.WriteLine($"PASS portable PE/PDB: {documents.Length} mapped documents, SourceLink={sourceLinkCount}, CodeView={codeView.Path}, InformationalVersion={informationalVersion}, SHA256={Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[0])))}");
