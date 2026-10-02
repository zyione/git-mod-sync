using System.IO;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModSync.Models;
using ModSync.Services;

namespace ModSync.Tests;

[TestClass]
public class FabricServiceTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ModSync_FabricTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch { }
    }

    [TestMethod]
    public void TestFabricStatusWhenNotConfigured()
    {
        var logger = new LoggingService();
        var configService = new ConfigService(logger);
        configService.Config.FabricLoaderVersion = null;

        var fabricService = new FabricService(configService, logger);
        var status = fabricService.DetectFabricStatus();

        Assert.IsFalse(status.IsConfigured);
        Assert.IsFalse(status.IsUpToDate);
    }

    [TestMethod]
    public void TestFabricDetectionInstalledEvenWhenTargetNotConfigured()
    {
        var logger = new LoggingService();
        var configService = new ConfigService(logger);
        configService.Config.FabricLoaderVersion = null;

        string mcDir = Path.Combine(_tempDir, ".minecraft");
        string versionsDir = Path.Combine(mcDir, "versions", "fabric-loader-0.16.9-1.21.1");
        Directory.CreateDirectory(versionsDir);

        string jsonContent = @"{
            ""id"": ""fabric-loader-0.16.9-1.21.1"",
            ""inheritsFrom"": ""1.21.1"",
            ""libraries"": [{ ""name"": ""net.fabricmc:fabric-loader:0.16.9"" }]
        }";
        File.WriteAllText(Path.Combine(versionsDir, "fabric-loader-0.16.9-1.21.1.json"), jsonContent);

        var fabricService = new TestableFabricService(configService, logger, mcDir);
        var status = fabricService.DetectFabricStatus();

        Assert.IsFalse(status.IsConfigured);
        Assert.IsTrue(status.IsMinecraftFound);
        Assert.AreEqual("0.16.9", status.InstalledLoaderVersion);
        Assert.AreEqual("1.21.1", status.MinecraftVersion);
        Assert.IsFalse(status.IsUpToDate);
    }

    [TestMethod]
    public void TestFabricDetectionInstalledMismatch()
    {
        var logger = new LoggingService();
        var configService = new ConfigService(logger);
        configService.Config.FabricLoaderVersion = "0.16.9";

        // Create mock .minecraft structure in temp directory
        string mcDir = Path.Combine(_tempDir, ".minecraft");
        string versionsDir = Path.Combine(mcDir, "versions", "fabric-loader-0.15.11-1.21.1");
        Directory.CreateDirectory(versionsDir);

        string jsonContent = @"
        {
            ""id"": ""fabric-loader-0.15.11-1.21.1"",
            ""inheritsFrom"": ""1.21.1"",
            ""libraries"": [
                {
                    ""name"": ""net.fabricmc:fabric-loader:0.15.11""
                }
            ]
        }";
        File.WriteAllText(Path.Combine(versionsDir, "fabric-loader-0.15.11-1.21.1.json"), jsonContent);

        // Subclass or test helper to supply mock mcDir
        var fabricService = new TestableFabricService(configService, logger, mcDir);
        var status = fabricService.DetectFabricStatus();

        Assert.IsTrue(status.IsConfigured);
        Assert.IsTrue(status.IsMinecraftFound);
        Assert.AreEqual("0.15.11", status.InstalledLoaderVersion);
        Assert.AreEqual("1.21.1", status.MinecraftVersion);
        Assert.AreEqual("0.16.9", status.TargetLoaderVersion);
        Assert.IsFalse(status.IsUpToDate);
    }

    [TestMethod]
    public void TestFabricDetectionUpToDate()
    {
        var logger = new LoggingService();
        var configService = new ConfigService(logger);
        configService.Config.FabricLoaderVersion = "0.16.9";

        string mcDir = Path.Combine(_tempDir, ".minecraft");
        string versionsDir = Path.Combine(mcDir, "versions", "fabric-loader-0.16.9-1.21.1");
        Directory.CreateDirectory(versionsDir);

        string jsonContent = @"
        {
            ""id"": ""fabric-loader-0.16.9-1.21.1"",
            ""inheritsFrom"": ""1.21.1"",
            ""libraries"": [
                {
                    ""name"": ""net.fabricmc:fabric-loader:0.16.9""
                }
            ]
        }";
        File.WriteAllText(Path.Combine(versionsDir, "fabric-loader-0.16.9-1.21.1.json"), jsonContent);

        var fabricService = new TestableFabricService(configService, logger, mcDir);
        var status = fabricService.DetectFabricStatus();

        Assert.IsTrue(status.IsConfigured);
        Assert.IsTrue(status.IsMinecraftFound);
        Assert.AreEqual("0.16.9", status.InstalledLoaderVersion);
        Assert.AreEqual("1.21.1", status.MinecraftVersion);
        Assert.IsTrue(status.IsUpToDate);
    }

    [TestMethod]
    public void TestFabricVersionPulledFromRepoFabricVersionTxt()
    {
        var logger = new LoggingService();
        var configService = new ConfigService(logger);
        configService.Config.FabricLoaderVersion = null; // No local config

        string repoDir = Path.Combine(_tempDir, "repository");
        Directory.CreateDirectory(repoDir);
        File.WriteAllText(Path.Combine(repoDir, "fabric-version.txt"), "0.19.5\r\n");

        string mcDir = Path.Combine(_tempDir, ".minecraft");
        string versionsDir = Path.Combine(mcDir, "versions", "fabric-loader-0.15.11-1.21.1");
        Directory.CreateDirectory(versionsDir);

        var fabricService = new TestableFabricService(configService, logger, mcDir, repoDir);
        var status = fabricService.DetectFabricStatus();

        Assert.IsTrue(status.IsConfigured);
        Assert.AreEqual("0.19.5", status.TargetLoaderVersion);
        Assert.AreEqual("Repository (fabric-version.txt)", status.VersionSource);
        Assert.AreEqual("0.15.11", status.InstalledLoaderVersion);
        Assert.IsFalse(status.IsUpToDate);
    }

    [TestMethod]
    public void TestFabricVersionCleansPrefixAndNewlines()
    {
        var logger = new LoggingService();
        var configService = new ConfigService(logger);
        configService.Config.FabricLoaderVersion = null;

        string repoDir = Path.Combine(_tempDir, "repository");
        Directory.CreateDirectory(repoDir);
        File.WriteAllText(Path.Combine(repoDir, "fabric-version.txt"), "  v0.19.5  \n# optional comment");

        string mcDir = Path.Combine(_tempDir, ".minecraft");

        var fabricService = new TestableFabricService(configService, logger, mcDir, repoDir);
        var (version, source) = fabricService.ResolveTargetFabricLoaderVersionLocalFast();

        Assert.AreEqual("0.19.5", version);
        Assert.AreEqual("Repository (fabric-version.txt)", source);
    }

    [TestMethod]
    public void TestMinecraftVersionPulledFromRepoMinecraftVersionTxt()
    {
        var logger = new LoggingService();
        var configService = new ConfigService(logger);

        string repoDir = Path.Combine(_tempDir, "repository");
        Directory.CreateDirectory(repoDir);
        File.WriteAllText(Path.Combine(repoDir, "minecraft-version.txt"), "1.20.1\r\n");

        string mcDir = Path.Combine(_tempDir, ".minecraft");

        var fabricService = new TestableFabricService(configService, logger, mcDir, repoDir);
        string mcVer = fabricService.ResolveTargetMinecraftVersionLocalFast();

        Assert.AreEqual("1.20.1", mcVer);
    }

    [TestMethod]
    public void TestMinecraftVersionDefaultsTo1201WhenNoOverride()
    {
        var logger = new LoggingService();
        var configService = new ConfigService(logger);

        string mcDir = Path.Combine(_tempDir, ".minecraft");
        var fabricService = new TestableFabricService(configService, logger, mcDir);
        string mcVer = fabricService.ResolveTargetMinecraftVersionLocalFast();

        Assert.AreEqual("1.20.1", mcVer);
    }

    [TestMethod]
    public void TestMinecraftVersionDetectedFromJarFilename()
    {
        var logger = new LoggingService();
        var configService = new ConfigService(logger);

        string repoDir = Path.Combine(_tempDir, "repository");
        Directory.CreateDirectory(repoDir);
        File.WriteAllText(Path.Combine(repoDir, "fabric-version.txt"), "0.19.5");
        File.WriteAllText(Path.Combine(repoDir, "AmbientSounds_FABRIC_v6.3.8_mc1.20.1.jar"), "dummy");

        string mcDir = Path.Combine(_tempDir, ".minecraft");

        var fabricService = new TestableFabricService(configService, logger, mcDir, repoDir);
        var status = fabricService.DetectFabricStatus();

        Assert.AreEqual("1.20.1", status.MinecraftVersion);
    }

    private class TestableFabricService : FabricService
    {
        public TestableFabricService(ConfigService cfg, LoggingService log, string customMcDir, string? customRepoDir = null)
            : base(cfg, log)
        {
            CustomMinecraftDirectory = customMcDir;
            CustomRepositoryDirectory = customRepoDir;
        }
    }
}
