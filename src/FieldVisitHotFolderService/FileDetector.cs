﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using Aquarius.TimeSeries.Client;
using Aquarius.TimeSeries.Client.ServiceModels.Provisioning;
using Common;
using FieldDataPluginFramework;
using FieldDataPluginFramework.Context;
using log4net;
using FieldDataPlugin = Aquarius.TimeSeries.Client.ServiceModels.Provisioning.FieldDataPlugin;
using ILog = log4net.ILog;

namespace FieldVisitHotFolderService
{
    public class FileDetector
    {
        private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public Context Context { get; set; }
        public CancellationToken CancellationToken { get; set; }

        private string SourceFolder { get; set; }
        private List<Regex> FileMasks { get; set; }
        private string ProcessingFolder { get; set; }
        private string PartialFolder { get; set; }
        private string UploadedFolder { get; set; }
        private string FailedFolder { get; set; }
        private List<IFieldDataPlugin> Plugins { get; set; }
        private IAquariusClient Client { get; set; }
        private List<LocationInfo> LocationCache { get; set; }

        public void Run()
        {
            Validate();

            WaitForStableServerVersion();

            if (CancellationToken.IsCancellationRequested)
                return;

            ThrowIfJsonPluginNotInstalled();

            while (!CancellationToken.IsCancellationRequested)
            {
                ProcessNewFiles();
                WaitForNewFiles();
            }
        }

        private void Validate()
        {
            if (string.IsNullOrWhiteSpace(Context.HotFolderPath))
                throw new ExpectedException($"You must specify a /{nameof(Context.HotFolderPath)} option.");

            Context.HotFolderPath = ResolvePath(FileHelper.ExeDirectory, Context.HotFolderPath);

            SourceFolder = Context.HotFolderPath;

            ThrowIfFolderIsMissing(SourceFolder);

            if (!Context.Plugins.Any())
                throw new ExpectedException($"You must specify a /Plugin option.");

            ProcessingFolder = CreateFolderPath(Context.ProcessingFolder);
            PartialFolder = CreateFolderPath(Context.PartialFolder);
            UploadedFolder = CreateFolderPath(Context.UploadedFolder);
            FailedFolder = CreateFolderPath(Context.FailedFolder);

            FileMasks = (Context.FileMask ?? "*.*")
                .Split(FileMaskDelimiters, StringSplitOptions.RemoveEmptyEntries)
                .Where(mask => !string.IsNullOrWhiteSpace(mask))
                .Select(CreateRegexFromDosWildcard)
                .ToList();

            Plugins = new PluginLoader()
                .LoadPlugins(Context.Plugins);

            Log.Info($"{Plugins.Count} local plugins ready for parsing field data files.");

            foreach (var plugin in Plugins)
            {
                Log.Info($"{plugin.GetType().AssemblyQualifiedName}");
            }
        }

        private static readonly char[] FileMaskDelimiters = {','};

        private static Regex CreateRegexFromDosWildcard(string mask)
        {
            return new Regex(
                $@"^{mask.Replace(".", "\\.").Replace("*", ".*")}$",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        }

        private string CreateFolderPath(string relativeOrAbsolutePath)
        {
            var path = ResolveSourceFolderPath(relativeOrAbsolutePath);

            if (!Directory.Exists(path))
            {
                Log.Info($"Creating '{path}'");
                Directory.CreateDirectory(path);
            }

            return path;
        }

        private string ResolveSourceFolderPath(string relativeOrAbsolutePath)
        {
            return ResolvePath(SourceFolder, relativeOrAbsolutePath);
        }

        private static string ResolvePath(string sourcePath, string relativeOrAbsolutePath)
        {
            return Path.IsPathRooted(relativeOrAbsolutePath)
                ? relativeOrAbsolutePath
                : Path.Combine(sourcePath, relativeOrAbsolutePath);
        }

        private void ThrowIfFolderIsMissing(string path)
        {
            if (!Directory.Exists(path))
                throw new ExpectedException($"'{path}' is not an existing folder.");
        }

        private IAquariusClient CreateConnectedClient()
        {
            Log.Info($"{FileHelper.ExeNameAndVersion} connecting to {Context.Server} as '{Context.Username}'");

            var client = AquariusClient.CreateConnectedClient(Context.Server, Context.Username, Context.Password);

            return client;
        }

        private void WaitForStableServerVersion()
        {
            var systemDetector = AquariusSystemDetector.Instance;
            systemDetector.Reset();

            for (var connectionAttempt = 1; ; ++connectionAttempt)
            {
                var serverType = systemDetector.GetAquariusServerType(Context.Server);

                if (serverType == AquariusServerType.Unknown)
                {
                    Log.Warn($"{Context.Server} is offline. Waiting {Context.ConnectionRetryDelay} before attempting next connection.");
                    CancellationToken.WaitHandle.WaitOne(Context.ConnectionRetryDelay);

                    if (CancellationToken.IsCancellationRequested)
                        return;

                    if (Context.MaximumConnectionAttempts > 0 && connectionAttempt >= Context.MaximumConnectionAttempts)
                        throw new ExpectedException($"Can't connect to {Context.Server} after {Context.MaximumConnectionAttempts} attempts.");

                    Log.Info($"Re-connecting with {Context.Server}");
                    continue;
                }

                var serverVersion = systemDetector.GetAquariusServerVersion(Context.Server);

                if (serverVersion.IsLessThan(MinimumVersion))
                    throw new ExpectedException($"{Context.Server} (v{serverVersion}) is below the minimum required version of v{MinimumVersion}");

                return;
            }
        }

        private static readonly AquariusServerVersion MinimumVersion = AquariusServerVersion.Create("18.4");

        private void ThrowIfJsonPluginNotInstalled()
        {
            using (Client = CreateConnectedClient())
            {
                GetInstalledJsonPlugin();
            }

            Client = null;
        }

        private FieldDataPlugin GetInstalledJsonPlugin()
        {
            var plugins = Client.Provisioning.Get(new GetFieldDataPlugins())
                .Results;

            var jsonPlugin = plugins
                .FirstOrDefault(p => p.AssemblyQualifiedTypeName.StartsWith("JsonFieldData.Plugin"));

            if (jsonPlugin == null)
                throw new ExpectedException($"The JSON field data plugin is not installed on {Context.Server}.\nDownload the latest plugin from https://github.com/AquaticInformatics/json-field-data-plugin/releases");

            return jsonPlugin;
        }

        private void ProcessNewFiles()
        {
            for (var files = GetNewFiles(); files.Any(); files = GetNewFiles())
            {
                Log.Info($"Processing {files.Count} files");

                WaitForStableServerVersion();

                if (CancellationToken.IsCancellationRequested)
                    return;

                LocationCache = new List<LocationInfo>();

                using (Client = CreateConnectedClient())
                {
                    foreach (var file in files)
                    {
                        if (CancellationToken.IsCancellationRequested)
                            return;

                        ProcessFile(file);
                    }
                }

                Client = null;
            }
        }

        private List<string> GetNewFiles()
        {
            return Directory.GetFiles(SourceFolder)
                .Where(f => FileMasks.Any(m => m.IsMatch(f)))
                .ToList();
        }

        public void ProcessFile(string filename)
        {
            var sourcePath = Path.Combine(SourceFolder, filename);

            if (!File.Exists(sourcePath))
                throw new ExpectedException($"'{sourcePath}' no longer exists");

            new FileProcessor
                {
                    Client = Client,
                    LocationCache = LocationCache,
                    Plugins = Plugins,
                    ProcessingFolder = ProcessingFolder,
                    PartialFolder = PartialFolder,
                    UploadedFolder = UploadedFolder,
                    FailedFolder = FailedFolder
                }
                .ProcessFile(sourcePath);
        }

        private void WaitForNewFiles()
        {
            if (CancellationToken.IsCancellationRequested)
                return;

            // TODO: Replace with an actual FileWatcher

            var timeSpan = Context.FileQuietDelay;
            Log.Info($"Waiting {timeSpan} for file activity to settle at '{SourceFolder}'");
            CancellationToken.WaitHandle.WaitOne(timeSpan);
        }
    }
}
