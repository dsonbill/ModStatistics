﻿using JsonFx.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using UnityEngine;
using KSP.UI;

namespace ModStatistics
{
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    internal class ModStatistics : MonoBehaviour
    {
        // The implementation with the highest version number will be allowed to run.
        private const int version = 8;
        private static int _version = version;

        private static readonly string folder;
        private static readonly string configpath;

        static ModStatistics()
        {
            folder = KSPUtil.ApplicationRootPath + "GameData" + Path.DirectorySeparatorChar + "ModStatistics" + Path.DirectorySeparatorChar;
            configpath = folder + "settings.cfg";
        }

        public void Start()
        {
            // Compatible types are identified by the type name and version field name.
            int highest =
                getAllTypes()
                .Where(t => t.Name == typeof(ModStatistics).Name)
                .Select(t => t.GetField("_version", BindingFlags.Static | BindingFlags.NonPublic))
                .Where(f => f != null)
                .Where(f => f.FieldType == typeof(int))
                .Max(f => (int)f.GetValue(null));

            // Let the latest version execute.
            if (version != highest) { return; }

            UnityEngine.Debug.Log(String.Format("[ModStatistics] Running version {0}", _version));

            //Works! Process.Start("python.exe");

            // Other checkers will see this version and not run.
            // This accomplishes the same as an explicit "ran" flag with fewer moving parts.
            _version = int.MaxValue;

            Directory.CreateDirectory(folder);

            var node = ConfigNode.Load(configpath);

            if (node == null)
            {
                promptPref();
            }
            else
            {
                var disabledString = node.GetValue("disabled");
                if (disabledString != null && bool.TryParse(disabledString, out disabled) && disabled)
                {
                    UnityEngine.Debug.Log("[ModStatistics] Disabled in configuration file");
                    return;
                }

                var idString = node.GetValue("id");
                try
                {
                    id = new Guid(idString);
                }
                catch
                {
                    UnityEngine.Debug.LogWarning("[ModStatistics] Could not parse ID");
                }

                var str = node.GetValue("update");
                if (str != null && bool.TryParse(str, out update))
                {
                    writeConfig();
                    //checkUpdates();
                }
                else
                {
                    promptPref();
                }
            }

            running = true;
            DontDestroyOnLoad(this);

            if (File.Exists(folder + "checkpoint.json"))
            {
                File.Move(folder + "checkpoint.json", createReportPath());
            }

            sendReports();
            install();
        }

        protected void promptPref()
        {
            //Enabled
            PopupDialog.SpawnPopupDialog(
                new MultiOptionDialog(
                    @"Ok, here's the deal:

One way or another, if intentional or not, ModStatistics has been installed into
your KSP GameData folder. ModStatistics is a very useful program for mod authors.
ModStatistics is not built to help indentify you - in fact, with censorship to the
output log, there's no identifiable information at all. At the end of the day,
ModStatistics is built to benefit your gameplay, and give you an all around
better KSP modding experience.

By choosing 'AGREE' below, you agree to allow <SERVER> to collect - periodically, 
over the internet, and completely anonymously:

Mod Usage and Installation, Errors, Exceptions, Crashes, KSP Version, Log File
(WITH CENSORED DIRECTORY), and your Program ID.

Your program ID is not traceable back to you, and is only used to identify individual
instances of KSP - this is so we can tell which reports are from the same game
installation. While this does mean we build information on your specific installation,
it also means we can identify problems from installation and uninstallation, as well
as find out what mods people are using together, identify errors from using mods
together, possible reasons for uninstallation, KSP version incompatibility, and much
more. This is all made to ultimately benefit you.

Some of this information can be turned off independently by the upcoming questions
in addition to config files, and some only by disabling ModStatistics completely,
either by config file or by choosing 'DISAGREE' below.

If you have agreed, the above information will be generated into a report at the
closing or crashing of KSP and sent to < SERVER > the next time KSP is opened.",
                    "ModStatistics - End-User License Agreement",
                    HighLogic.UISkin,
                    new DialogGUIButton("AGREE", () => { enabledChoice(true); writeConfig(); }, true),
                    new DialogGUIButton("DISAGREE", () => { enabledChoice(false); updateChoice(false); writeConfig(); }, true)
                    ),
                true,
                HighLogic.UISkin
            );

            if (!enabled)
            {
                return;
            }

            PopupDialog.SpawnPopupDialog(
                new MultiOptionDialog(
                    @"Would you like ModStatistics to automatically update when new versions are available?
This makes sure your version is set to the correct one, and ensures that mod authors get the most relavant information
possible. Please consider accepting. Updates will be automatically downloaded from: < SERVER >",
                    "ModStatistics - Automatic Updates",
                    HighLogic.UISkin,
                    new DialogGUIButton("Allow Updates", () => { updateChoice(true); writeConfig(); }, true), // checkUpdates(); }, true),
                    new DialogGUIButton("Do Not Allow Updates", () => { updateChoice(false); writeConfig(); }, true)
                    ),
                true,
                HighLogic.UISkin
            );
        }

        protected void updateChoice(bool choice)
        {
            update = choice;
        }

        protected void enabledChoice(bool choice)
        {
            disabled = !choice;
        }

        private void writeConfig()
        {
            var text = String.Format("// To disable ModStatistics, change the line below to \"disabled = true\"" + Environment.NewLine + "// Do NOT delete the ModStatistics folder. It could be reinstated by another mod." + Environment.NewLine + "disabled = {2}" + Environment.NewLine + "update = {1}" + Environment.NewLine + "id = {0:N}" + Environment.NewLine, id, update.ToString().ToLower(), disabled.ToString().ToLower());
            File.WriteAllText(configpath, text);
        }

        private static IEnumerable<Type> getAllTypes()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (Exception)
                {
                    types = Type.EmptyTypes;
                }

                foreach (var type in types)
                {
                    yield return type;
                }
            }
        }

        private bool running = false;
        private bool disabled = false;
        private bool update = true;

        private Guid id = Guid.NewGuid();
        private GameScenes? scene = null;
        private DateTime started = DateTime.UtcNow;
        private DateTime sceneStarted = DateTime.UtcNow;
        private Dictionary<GameScenes, TimeSpan> sceneTimes = new Dictionary<GameScenes, TimeSpan>();
        private DateTime nextSave = DateTime.MinValue;

        public void FixedUpdate()
        {
            if (!running) { return; }

            if (scene != HighLogic.LoadedScene)
            {
                updateSceneTimes();
            }

            var now = DateTime.UtcNow;
            if (nextSave < now)
            {
                nextSave = now.AddSeconds(15);

                var report = prepareReport(true);
                File.WriteAllText(folder + "checkpoint.json", report);
            }
        }

        public void OnDestroy()
        {
            if (!running) { return; }

            UnityEngine.Debug.Log("[ModStatistics] Saving report");
            File.WriteAllText(createReportPath(), prepareReport(false));

            File.Delete(folder + "checkpoint.json");
        }

        private static string createReportPath()
        {
            int i = 0;
            string path;
            do
            {
                path = folder + "report-" + i + ".json";
                i++;
            } while (File.Exists(path));
            return path;
        }

        private void sendReports()
        {
            List<string> files = Directory.GetFiles(folder, "report-*.json").ToList<string>();
            for (int i = 0; i < files.Count; i++)
            {
                sendFile(files, i);
            }
        }

        protected void sendFile(List<string> files, int index)
        {
            using (var client = new WebClient())
            {
                setUserAgent(client);
                client.Headers.Add(HttpRequestHeader.ContentType, "application/json");

                client.UploadStringCompleted += (s, e) =>
                {
                    var file = (string)e.UserState;
                    if (e.Cancelled)
                    {
                        UnityEngine.Debug.LogWarning(String.Format("[ModStatistics] Upload operation for {0} was cancelled", Path.GetFileName(file)));
                    }
                    else if (e.Error != null)
                    {
                        UnityEngine.Debug.LogError(String.Format("[ModStatistics] Could not upload {0}:\n{1}", Path.GetFileName(file), e.Error));
                    }
                    else
                    {
                        UnityEngine.Debug.Log("[ModStatistics] " + Path.GetFileName(file) + " sent successfully");
                        File.Delete(file);
                    }
                };

                try
                {
                    client.UploadStringAsync(new Uri(@"http://localhost:5000/statistics/report"), null, File.ReadAllText(files[index]), files[index]);
                }
                catch (Exception e)
                {
                    UnityEngine.Debug.LogWarning(String.Format("[ModStatistics] Error initiating {0) upload:\n{1}", Path.GetFileName(files[index]), e));
                }
            }
        }

        private static void setUserAgent(WebClient client)
        {
            client.Headers.Add(HttpRequestHeader.UserAgent, String.Format("ModStatistics/{0} ({1})", getInformationalVersion(Assembly.GetExecutingAssembly()), version));
        }

        private class ManifestEntry
        {
            public string url = String.Empty;
            public string path = String.Empty;
        }

        private void checkUpdates()
        {
            if (!update) { return; }

            using (var client = new WebClient())
            {
                client.DownloadStringCompleted += (s, e) =>
                {
                    if (e.Cancelled)
                    {
                        UnityEngine.Debug.LogWarning(String.Format("[ModStatistics] Update query operation was cancelled"));
                    }
                    else if (e.Error != null)
                    {
                        UnityEngine.Debug.LogError(String.Format("[ModStatistics] Could not query for updates:\n{0}", e.Error));
                    }
                    else
                    {
                        try
                        {
                            var manifest = new JsonReader().Read<ManifestEntry[]>(e.Result);
                            foreach (var entry in manifest)
                            {
                                var dest = folder + Path.DirectorySeparatorChar + entry.path.Replace('/', Path.DirectorySeparatorChar);
                                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                                setUserAgent(client);
                                client.DownloadFileAsync(new Uri(entry.url), dest, entry);
                            }
                        }
                        catch (Exception ex)
                        {
                            UnityEngine.Debug.LogError(String.Format("[ModStatistics] Error parsing update manifest:\n{0}", ex));
                        }
                    }
                };

                client.DownloadFileCompleted += (s, e) =>
                {
                    var entry = e.UserState as ManifestEntry;
                    if (e.Cancelled)
                    {
                        UnityEngine.Debug.LogWarning(String.Format("[ModStatistics] Update download operation was cancelled"));
                    }
                    else if (e.Error != null)
                    {
                        UnityEngine.Debug.LogError(String.Format("[ModStatistics] Could not download update for {0}:\n{1}", entry.path, e.Error));
                    }
                    else
                    {
                        UnityEngine.Debug.Log("[ModStatistics] Successfully updated " + entry.path);
                    }
                };

                setUserAgent(client);
                client.DownloadStringAsync(new Uri(@"http://stats.majiir.net/update"));
            }
        }

        private void install()
        {
            var dest = folder + "Plugins" + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(dest);
            if (!File.Exists(dest + "JsonFx.dll"))
            {
                var fxpath = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "JsonFx").Location;
                File.Copy(fxpath, dest + "JsonFx.dll");
            }
            var mspath = dest + "ModStatistics-" + getInformationalVersion(Assembly.GetExecutingAssembly()) + ".dll";
            if (!File.Exists(mspath))
            {
                File.Copy(Assembly.GetExecutingAssembly().Location, mspath);
            }
        }

        private void updateSceneTimes()
        {
            var lastScene = scene;
            var lastStarted = sceneStarted;
            scene = HighLogic.LoadedScene;
            sceneStarted = DateTime.UtcNow;

            if (lastScene == null) { return; }

            if (!sceneTimes.ContainsKey(lastScene.Value))
            {
                sceneTimes[lastScene.Value] = TimeSpan.Zero;
            }

            sceneTimes[lastScene.Value] += (sceneStarted - lastStarted);
        }

        private object[] assembliesInfo = null;

        private string prepareReport(bool crashed)
        {
            updateSceneTimes();

            if (assembliesInfo == null)
            {
                assembliesInfo = (from assembly in AssemblyLoader.loadedAssemblies.Skip(1)
                                  let fileVersion = assembly.assembly.GetName().Version
                                  select new
                                  {
                                      dllName = assembly.dllName,
                                      name = assembly.name,
                                      title = getAssemblyTitle(assembly.assembly),
                                      url = assembly.url,
                                      sha2 = getAssemblyHash(assembly.assembly),
                                      kspVersionMajor = assembly.versionMajor,
                                      kspVersionMinor = assembly.versionMinor,
                                      fileVersion = new
                                      {
                                          major = fileVersion.Major,
                                          minor = fileVersion.Minor,
                                          revision = fileVersion.Revision,
                                          build = fileVersion.Build,
                                      },
                                      informationalVersion = getInformationalVersion(assembly.assembly)
                                  }).ToArray();
            }

            var report = new
            {
                started = started,
                finished = sceneStarted,
                crashed = crashed,
                statisticsVersion = version,
                platform = getRunningPlatform(),
                id = id.ToString("N"),
                installedWithSteam = installedWithSteam(),
                gameVersion = new
                {
                    build = Versioning.BuildID,
                    major = Versioning.version_major,
                    minor = Versioning.version_minor,
                    revision = Versioning.Revision,
                    experimental = Versioning.Experimental,
                    isBeta = Versioning.isBeta,
                    isSteam = Versioning.IsSteam,
                    is64 = IntPtr.Size == 8,
                },
                scenes = sceneTimes.OrderBy(p => p.Key).ToDictionary(p => p.Key.ToString().ToLower(), p => p.Value.TotalMilliseconds),
                systemInfo = new {
                    cpus = SystemInfo.processorCount,
                    gpuMemory = SystemInfo.graphicsMemorySize,
                    gpuVendorId = SystemInfo.graphicsDeviceVendorID,
                    systemMemory = SystemInfo.systemMemorySize,
                },
                assemblies = assembliesInfo
            };

            return new JsonWriter().Write(report);
        }

        private static string getInformationalVersion(Assembly assembly)
        {
            return System.Diagnostics.FileVersionInfo.GetVersionInfo(assembly.Location).ProductVersion;
        }

        private static HashSet<String> warnedAssemblies = new HashSet<String>();

        private static string getAssemblyTitle(Assembly assembly)
        {
            try
            {
                var attr = assembly.GetCustomAttributes(typeof(AssemblyTitleAttribute), false).OfType<AssemblyTitleAttribute>().FirstOrDefault();
                if (attr == null) { return String.Empty; }
                return attr.Title;
            }
            catch (TypeLoadException e)
            {
                var name = assembly.GetName().Name;
                if (!warnedAssemblies.Contains(name))
                {
                    warnedAssemblies.Add(name);
                    UnityEngine.Debug.LogError(String.Format("[ModStatistics] Error while inspecting assembly {0}. This probably means that {0} is targeting a runtime other than .NET 3.5. Please notify the author of {0} of this error.\n\n{1}", name, e));
                }
                return null;
            }
        }

        private enum Platform
        {
            Windows,
            Linux,
            Mac
        }

        private static Platform getRunningPlatform()
        {
            var platform = Environment.OSVersion.Platform;
            if (platform == PlatformID.Unix)
            {
                if (Directory.Exists("/Applications") && Directory.Exists("/Users") && Directory.Exists("/Volumes") && Directory.Exists("/System"))
                {
                    return Platform.Mac;
                }
                else
                {
                    return Platform.Linux;
                }
            }
            else if (platform == PlatformID.MacOSX)
            {
                return Platform.Mac;
            }
            else
            {
                return Platform.Windows;
            }
        }

        private static string getAssemblyHash(Assembly assembly)
        {
            byte[] hash;
            using (var sha2 = SHA256.Create()) {
                using (var stream = File.OpenRead(assembly.Location)) {
                    hash = sha2.ComputeHash(stream);
                }
            }
            var sb = new StringBuilder();
            foreach (var b in hash)
            {
                sb.Append(b.ToString("X2"));
            }
            return sb.ToString();
        }

        private static bool installedWithSteam()
        {
            var path = KSPUtil.ApplicationRootPath;
            return path.Contains(@"SteamApps\common") || path.Contains(@"SteamApps/common");
        }
    }
}