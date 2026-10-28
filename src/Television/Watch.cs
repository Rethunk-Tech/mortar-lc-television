using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Television;

// Watches the pack folders so files dropped in while the game runs show up without a restart. Changes are gathered
// and handed over on the main thread once they've been quiet for a second, since a copy writes a file in bursts.
internal sealed class Watch : MonoBehaviour
{
    private static readonly TimeSpan quiet = TimeSpan.FromSeconds(1);

    private readonly List<FileSystemWatcher> watchers = [];
    private readonly object gate = new();
    private readonly HashSet<string> changed = [];
    private DateTime last;
    private Action<IReadOnlyCollection<string>> onChange = _ => { };

    public static void Start(GameObject host, Action<IReadOnlyCollection<string>> onChange, params string[] dirs)
    {
        Watch watch = host.AddComponent<Watch>();
        watch.onChange = onChange;
        foreach (string dir in dirs.Where(Directory.Exists))
        {
            try
            {
                var watcher = new FileSystemWatcher(dir) { IncludeSubdirectories = true };
                watcher.Created += (_, e) => watch.Note(e.FullPath);
                watcher.Changed += (_, e) => watch.Note(e.FullPath);
                watcher.Deleted += (_, e) => watch.Note(e.FullPath);
                watcher.Renamed += (_, e) =>
                {
                    watch.Note(e.OldFullPath);
                    watch.Note(e.FullPath);
                };
                watcher.EnableRaisingEvents = true;
                watch.watchers.Add(watcher);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Can't watch {dir} for new files, so they need a restart: {e.Message}");
            }
        }
    }

    // Runs on the watcher's own thread.
    private void Note(string path)
    {
        lock (gate)
        {
            changed.Add(path);
            last = DateTime.UtcNow;
        }
    }

    private void Update()
    {
        string[] batch;
        lock (gate)
        {
            if (changed.Count == 0 || DateTime.UtcNow - last < quiet)
            {
                return;
            }
            batch = [.. changed];
            changed.Clear();
        }
        try
        {
            onChange(batch);
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"Couldn't pick up changed files: {e}");
        }
    }

    private void OnDestroy()
    {
        foreach (FileSystemWatcher watcher in watchers)
        {
            watcher.Dispose();
        }
    }
}
