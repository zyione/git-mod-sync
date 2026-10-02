using System.Text.Json;
using ModSync.Services;

// create base.exe target.exe output.patch; apply base.exe patch output.exe
if (args.Length != 4 || (args[0] != "create" && args[0] != "apply"))
    throw new ArgumentException("Usage: DeltaBuilder create|apply base input output");
if (args[0] == "create") BinaryDelta.Create(args[1], args[2], args[3]);
else BinaryDelta.Apply(args[1], args[2], args[3]);
Console.WriteLine(JsonSerializer.Serialize(new { size = new FileInfo(args[3]).Length, sha256 = BinaryDelta.Hash(args[3]) }));
