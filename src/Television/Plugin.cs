using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using ConfigSync;
using GameNetcodeStuff;
using MonoMod.RuntimeDetour;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Video;

namespace Television;

[BepInPlugin(MyPluginInfo.Id, MyPluginInfo.Name, MyPluginInfo.Version)]
[BepInProcess("Lethal Company.exe")]
[BepInDependency("tech.rethunk.mortar.ConfigSync")]
public class Plugin : BaseUnityPlugin
{
    internal static ManualLogSource Log { get; private set; } = null!;

    private const float MeasureTimeout = 10f;
    private const string Message = "tech.rethunk.mortar.Television";
    private const byte Ask = 0, Skip = 1, State = 2;

    private static readonly List<Hook> hooks = [];
    private static string[] videos = [];
    // Each video's length in seconds; 0 for one that can't play. Null until measured.
    private static double[]? lengths;
    private static bool measuring;
    // Lengths already measured, by file, so a reload only opens the new ones; and which measure is current.
    private static readonly Dictionary<string, double> known = [];
    private static int measureRound;
    private static SyncedEntry<bool> channel = null!, mixGame = null!, shuffle = null!;
    // With the channel off, the show each TV plays next.
    private static readonly Dictionary<TVScript, int> nextShow = [];
    // Added to the server's clock in channel mode, so skipping ahead moves everyone's channel together.
    private static double offset;
    private static bool listening;
    // In channel mode a player plays the host's schedule, so different packs don't put anyone out of step.
    private static List<(string Key, double Length)>? hostSchedule;
    private static string? playingKey;
    private static int darkToken;
    private static Runner runner = null!;
    private const float SyncEvery = 5f;
    private const double Drift = 1.5;
    private static readonly Dictionary<TVScript, InteractTrigger> triggers = [];
    private static readonly Dictionary<InteractTrigger, string> hoverTips = [];

    private void Awake()
    {
        Log = Logger;
        videos = Videos();
        Log.LogInfo($"{MyPluginInfo.Name} {MyPluginInfo.Version} found {videos.Length} videos");
        var shared = SyncedConfig.For(this);
        channel = shared.Share(Config.Bind("TV", "Channel", true,
            "Play the videos as one channel on the lobby's clock, so everyone sees the same moment. Off starts a video from its beginning each time the TV comes on."));
        mixGame = shared.Share(Config.Bind("TV", "Include game clips", false,
            "Keep the TV's own clips in the schedule with the packs' videos."));
        shuffle = shared.Share(Config.Bind("TV", "Shuffle", true,
            "Play the videos in an order picked from the lobby's seed instead of by file name."));
        Action replay = () =>
        {
            foreach (TVScript tv in FindObjectsOfType<TVScript>())
            {
                if (tv.wasTvOnLastFrame)
                {
                    Play(tv);
                }
            }
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer && NetworkManager.Singleton.CustomMessagingManager != null)
            {
                SendStateToAll();
            }
        };
        channel.Changed += replay;
        mixGame.Changed += replay;
        shuffle.Changed += replay;
        var holder = new GameObject("Television");
        DontDestroyOnLoad(holder);
        holder.hideFlags = HideFlags.HideAndDontSave;
        runner = holder.AddComponent<Runner>();
        Watch.Start(holder, Reload, Paths.PluginPath);
        if (videos.Length > 0)
        {
            TakeOver();
        }
    }

    // Each pack's mortar/tv folder. The folders TVLoader reads are still read, so its packs work here too.
    private static string[] Videos() =>
        Directory.GetDirectories(Paths.PluginPath, "mortar", SearchOption.AllDirectories)
            .Select(dir => Path.Combine(dir, "tv"))
            .Where(Directory.Exists)
            .Concat(Directory.GetDirectories(Paths.PluginPath, "Television Videos", SearchOption.AllDirectories))
            .SelectMany(Directory.GetFiles)
            .Where(IsVideo)
            .Distinct()
            .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
            .ToArray();

    private static bool IsVideo(string file) =>
        file.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".webm", StringComparison.OrdinalIgnoreCase);

    // Without a single video the game's TV is left alone; the first one dropped in hands it over.
    private static void TakeOver()
    {
        if (hooks.Count > 0)
        {
            return;
        }
        const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        hooks.Add(new Hook(typeof(TVScript).GetMethod("Update", any), Update));
        hooks.Add(new Hook(typeof(TVScript).GetMethod(nameof(TVScript.TurnTVOnOff)), TurnOnOff));
        hooks.Add(new Hook(typeof(TVScript).GetMethod("TVFinishedClip", any), Finished));
        hooks.Add(new Hook(typeof(StartOfRound).GetMethod("Start", any), ShipLoaded));
        // Measure now so the first switch-on doesn't wait.
        StartMeasure();
    }

    // New and changed videos are measured; the ones already measured keep their lengths.
    private static void Reload(IReadOnlyCollection<string> changed)
    {
        if (!changed.Any(f => IsVideo(f) || Path.GetExtension(f) == ""))
        {
            return;
        }
        if (lengths != null)
        {
            for (int i = 0; i < videos.Length; i++)
            {
                if (!changed.Contains(videos[i]))
                {
                    known[videos[i]] = lengths[i];
                }
            }
        }
        foreach (string file in changed)
        {
            known.Remove(file);
        }
        videos = Videos();
        Log.LogInfo($"Found {videos.Length} videos");
        lengths = null;
        measuring = false;
        measureRound++;
        if (videos.Length > 0)
        {
            TakeOver();
            StartMeasure();
        }
    }

    private static void ShipLoaded(Action<StartOfRound> orig, StartOfRound self)
    {
        orig(self);
        offset = 0;
        hostSchedule = null;
        playingKey = null;
        nextShow.Clear();
        triggers.Clear();
        hoverTips.Clear();
        NetworkManager network = NetworkManager.Singleton;
        if (network?.CustomMessagingManager == null)
        {
            return;
        }
        network.CustomMessagingManager.RegisterNamedMessageHandler(Message, Received);
        if (!network.IsServer)
        {
            Send(NetworkManager.ServerClientId, Ask);
        }
        if (!listening)
        {
            // Q is the game's button for an item's second use. While you're looking at the TV, it changes channel.
            IngamePlayerSettings.Instance.playerInput.actions.FindAction("ItemSecondaryUse").performed += Pressed;
            listening = true;
        }
    }

    private static void Pressed(InputAction.CallbackContext context)
    {
        PlayerControllerB? player = GameNetworkManager.Instance?.localPlayerController;
        if (player == null || player.isTypingChat || player.inTerminalMenu || player.hoveringOverTrigger == null)
        {
            return;
        }
        TVScript? tv = TvOf(player.hoveringOverTrigger);
        if (tv == null || !tv.tvOn)
        {
            return;
        }
        if (NetworkManager.Singleton.IsServer)
        {
            SkipAhead(tv);
        }
        else
        {
            Send(NetworkManager.ServerClientId, Skip);
        }
    }

    private static TVScript? TvOf(InteractTrigger trigger)
    {
        for (int i = 0; i < trigger.onInteract.GetPersistentEventCount(); i++)
        {
            if (trigger.onInteract.GetPersistentTarget(i) is TVScript tv)
            {
                return tv;
            }
        }
        return null;
    }

    // The host jumps to the start of the next show (or one show on, with the channel off) and tells everyone.
    private static void SkipAhead(TVScript tv)
    {
        List<Show> shows = lengths == null ? [] : Schedule(tv);
        if (shows.Count == 0)
        {
            return;
        }
        if (channel.Value)
        {
            double at = (NetworkManager.Singleton.ServerTime.Time + offset) % shows.Sum(show => show.Length);
            int i = 0;
            while (i < shows.Count - 1 && at >= shows[i].Length)
            {
                at -= shows[i].Length;
                i++;
            }
            offset += shows[i].Length - at + 0.05;
        }
        SendStateToAll(advance: !channel.Value);
        if (tv.wasTvOnLastFrame)
        {
            Play(tv);
        }
    }

    // From the host, State also carries the schedule: each show's name and length.
    private static void Send(ulong to, byte kind, bool advance = false)
    {
        using var message = new FastBufferWriter(64, Allocator.Temp, 1 << 16);
        message.WriteValueSafe(kind);
        message.WriteValueSafe(offset);
        message.WriteValueSafe(advance);
        TVScript? tv = FindObjectOfType<TVScript>();
        List<Show> shows = kind == State && tv != null && lengths != null ? Schedule(tv) : [];
        message.WriteValueSafe(shows.Count);
        foreach (Show show in shows)
        {
            message.WriteValueSafe(show.Key);
            message.WriteValueSafe(show.Length);
        }
        NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage(Message, to, message, NetworkDelivery.ReliableFragmentedSequenced);
    }

    private static void SendStateToAll(bool advance = false)
    {
        NetworkManager network = NetworkManager.Singleton;
        foreach (ulong client in network.ConnectedClientsIds)
        {
            if (client != NetworkManager.ServerClientId)
            {
                Send(client, State, advance);
            }
        }
    }

    private static void Received(ulong sender, FastBufferReader reader)
    {
        reader.ReadValueSafe(out byte kind);
        reader.ReadValueSafe(out double sent);
        reader.ReadValueSafe(out bool advance);
        TVScript? tv = FindObjectOfType<TVScript>();
        if (NetworkManager.Singleton.IsServer)
        {
            if (kind == Ask)
            {
                Send(sender, State);
            }
            else if (kind == Skip && tv != null && tv.tvOn)
            {
                SkipAhead(tv);
            }
            return;
        }
        reader.ReadValueSafe(out int count);
        var schedule = new List<(string Key, double Length)>(count);
        for (int i = 0; i < count; i++)
        {
            reader.ReadValueSafe(out string key);
            reader.ReadValueSafe(out double length);
            schedule.Add((key, length));
        }
        bool changed = sent != offset || hostSchedule == null || !hostSchedule.SequenceEqual(schedule);
        offset = sent;
        hostSchedule = schedule.Count > 0 ? schedule : null;
        if (tv == null || !tv.wasTvOnLastFrame)
        {
            return;
        }
        if (advance || (channel.Value && (changed || OutOfStep(tv))))
        {
            Play(tv);
        }
    }

    // Whether this player is on another show than the host's clock says, or too far from its place in it.
    private static bool OutOfStep(TVScript tv)
    {
        if (hostSchedule == null)
        {
            return false;
        }
        (int i, double at) = Locate(hostSchedule.Select(s => s.Length).ToList());
        return hostSchedule[i].Key != playingKey || (tv.video.isPlaying && Math.Abs(tv.video.time - at) > Drift);
    }

    // Which show the channel is on and how far into it, from the server's clock.
    private static (int Index, double At) Locate(List<double> lengths)
    {
        double at = (NetworkManager.Singleton.ServerTime.Time + offset) % lengths.Sum();
        int i = 0;
        while (i < lengths.Count - 1 && at >= lengths[i])
        {
            at -= lengths[i];
            i++;
        }
        return (i, at);
    }

    // Add the channel button and what's on to the TV's hover text.
    private static void Hover(TVScript tv, string? name)
    {
        if (!triggers.TryGetValue(tv, out InteractTrigger trigger))
        {
            trigger = FindObjectsOfType<InteractTrigger>().FirstOrDefault(t => TvOf(t) == tv);
            if (trigger == null)
            {
                return;
            }
            triggers[tv] = trigger;
            hoverTips[trigger] = trigger.hoverTip;
        }
        string original = hoverTips[trigger];
        trigger.hoverTip = name == null ? original : $"{original}\nNext channel : [Q]\n{name}";
    }

    private static void StartMeasure()
    {
        if (measuring || lengths != null)
        {
            return;
        }
        measuring = true;
        // On an object that outlives the ship, so leaving mid-measure doesn't leave the TV dark.
        var holder = new GameObject("Television lengths");
        DontDestroyOnLoad(holder);
        holder.hideFlags = HideFlags.HideAndDontSave;
        holder.AddComponent<Measurer>().StartCoroutine(Measure(holder, measureRound));
    }

    // The game still syncs whether the TV is on; this decides what it shows. Like the game, it goes dark for a player
    // inside the facility.
    private static void Update(Action<TVScript> orig, TVScript self)
    {
        GameNetworkManager game = GameNetworkManager.Instance;
        if (game == null || game.localPlayerController == null || NetworkManager.Singleton == null || NetworkManager.Singleton.ShutdownInProgress)
        {
            return;
        }
        bool show = self.tvOn && !game.localPlayerController.isInsideFactory;
        if (show == self.wasTvOnLastFrame)
        {
            return;
        }
        self.wasTvOnLastFrame = show;
        self.SetTVScreenMaterial(show);
        if (show)
        {
            Play(self);
        }
        else
        {
            self.video.Stop();
            StopGameAudio(self);
            Hover(self, null);
        }
    }

    private static void TurnOnOff(Action<TVScript, bool> orig, TVScript self, bool on)
    {
        self.tvOn = on;
        AudioClip click = on ? self.switchTVOn : self.switchTVOff;
        self.tvSFX.PlayOneShot(click);
        WalkieTalkie.TransmitOneShotAudio(self.tvSFX, click);
    }

    private static void Finished(Action<TVScript, VideoPlayer> orig, TVScript self, VideoPlayer source)
    {
        if (self.wasTvOnLastFrame)
        {
            Play(self);
        }
    }

    // With the channel on, the shows play back to back on the server's clock, so everyone sees the same moment, even
    // someone who just joined or turned the TV on.
    private static void Play(TVScript tv)
    {
        if (lengths == null)
        {
            StartMeasure();
            return;
        }
        List<Show> shows = Schedule(tv);
        if (channel.Value && hostSchedule != null && !NetworkManager.Singleton.IsServer)
        {
            (int i, double at) = Locate(hostSchedule.Select(s => s.Length).ToList());
            (string key, double length) = hostSchedule[i];
            Show? mine = AllShows(tv).FirstOrDefault(show => show.Key == key);
            if (mine != null)
            {
                PlayShow(tv, mine, at);
            }
            else
            {
                Dark(tv, key, length - at);
            }
            return;
        }
        if (shows.Count == 0)
        {
            return;
        }
        if (channel.Value)
        {
            (int i, double at) = Locate(shows.Select(show => show.Length).ToList());
            PlayShow(tv, shows[i], at);
        }
        else
        {
            nextShow.TryGetValue(tv, out int i);
            i %= shows.Count;
            nextShow[tv] = i + 1;
            PlayShow(tv, shows[i], 0);
        }
    }

    // A show the host has and this player doesn't (or can't play) keeps its slot as a dark screen.
    private static void Dark(TVScript tv, string key, double seconds)
    {
        tv.video.Stop();
        StopGameAudio(tv);
        tv.SetTVScreenMaterial(false);
        Hover(tv, null);
        playingKey = key;
        runner.StartCoroutine(AfterDark(tv, ++darkToken, (float)seconds));
    }

    private static IEnumerator AfterDark(TVScript tv, int token, float seconds)
    {
        yield return new WaitForSeconds(seconds);
        if (token == darkToken && tv != null && tv.wasTvOnLastFrame)
        {
            Play(tv);
        }
    }

    // Every show this player could play, whatever the settings: the game's clips and each pack video that plays here.
    private static IEnumerable<Show> AllShows(TVScript tv)
    {
        for (int i = 0; i < tv.tvClips.Length; i++)
        {
            yield return new Show(null, tv.tvClips[i], tv.tvAudioClips[i], tv.tvClips[i].length);
        }
        for (int i = 0; i < videos.Length; i++)
        {
            if (lengths![i] > 0)
            {
                yield return new Show(videos[i], null, null, lengths[i]);
            }
        }
    }

    // The game's clips first when they're in, then each pack video that plays here; shuffled by the lobby's seed, which
    // every player has.
    private static List<Show> Schedule(TVScript tv)
    {
        var shows = new List<Show>();
        // With no pack video that plays here, the game's own clips keep the TV from going dark.
        if (mixGame.Value || lengths!.All(length => length <= 0))
        {
            for (int i = 0; i < tv.tvClips.Length; i++)
            {
                shows.Add(new Show(null, tv.tvClips[i], tv.tvAudioClips[i], tv.tvClips[i].length));
            }
        }
        for (int i = 0; i < videos.Length; i++)
        {
            if (lengths![i] > 0)
            {
                shows.Add(new Show(videos[i], null, null, lengths[i]));
            }
        }
        if (shuffle.Value && StartOfRound.Instance != null)
        {
            var random = new System.Random(StartOfRound.Instance.randomMapSeed);
            shows = [.. shows.OrderBy(_ => random.Next())];
        }
        return shows;
    }

    // A pack video carries its own sound; the game's clips play theirs on the TV's speaker beside the picture.
    private static void PlayShow(TVScript tv, Show show, double at)
    {
        VideoPlayer video = tv.video;
        darkToken++;
        playingKey = show.Key;
        tv.SetTVScreenMaterial(true);
        // Stop it first so the sound settings below apply to the next show, not the one that's playing.
        video.Stop();
        StopGameAudio(tv);
        if (show.Clip != null)
        {
            video.source = VideoSource.VideoClip;
            video.clip = show.Clip;
            video.audioOutputMode = VideoAudioOutputMode.None;
            tv.tvSFX.clip = show.Audio;
            tv.tvSFX.time = (float)at;
            tv.tvSFX.Play();
        }
        else
        {
            // Route the sound before opening the file. The other way round, Unity plays the picture with no sound.
            video.audioOutputMode = VideoAudioOutputMode.AudioSource;
            video.controlledAudioTrackCount = 1;
            video.EnableAudioTrack(0, true);
            video.SetTargetAudioSource(0, tv.tvSFX);
            video.source = VideoSource.Url;
            video.url = new Uri(show.Url!).AbsoluteUri;
        }
        video.isLooping = false;
        video.time = at;
        video.Play();
        Hover(tv, show.Name);
    }

    private static void StopGameAudio(TVScript tv)
    {
        if (tv.tvSFX.clip != null)
        {
            tv.tvSFX.Stop();
            tv.tvSFX.clip = null;
        }
    }

    private sealed class Show(string? url, VideoClip? clip, AudioClip? audio, double length)
    {
        public string? Url { get; } = url;
        public VideoClip? Clip { get; } = clip;
        public AudioClip? Audio { get; } = audio;
        public double Length { get; } = length;
        public string Name => Url != null ? Path.GetFileNameWithoutExtension(Url) : Clip!.name;
        public string Key => Url != null ? Path.GetFileName(Url) : "game:" + Clip!.name;
    }

    // A video's length is only known once Unity has opened it, so each is opened once on a hidden player.
    private static IEnumerator Measure(GameObject holder, int round)
    {
        VideoPlayer probe = holder.AddComponent<VideoPlayer>();
        probe.playOnAwake = false;
        probe.audioOutputMode = VideoAudioOutputMode.None;
        probe.source = VideoSource.Url;
        bool failed = false;
        probe.errorReceived += (_, _) => failed = true;
        string[] list = videos;
        double[] measured = new double[list.Length];
        for (int i = 0; i < list.Length; i++)
        {
            if (known.TryGetValue(list[i], out measured[i]))
            {
                continue;
            }
            failed = false;
            probe.url = new Uri(list[i]).AbsoluteUri;
            probe.Prepare();
            float waited = 0;
            while (!probe.isPrepared && !failed && waited < MeasureTimeout)
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }
            measured[i] = probe.isPrepared ? probe.length : 0;
            if (measured[i] <= 0)
            {
                Log.LogWarning($"{list[i]} can't play here, so it's skipped");
            }
        }
        Destroy(holder);
        // A reload started a newer measure.
        if (round != measureRound)
        {
            yield break;
        }
        lengths = measured;
        TVScript tv = FindObjectOfType<TVScript>();
        if (tv != null && tv.wasTvOnLastFrame)
        {
            Play(tv);
        }
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer && NetworkManager.Singleton.CustomMessagingManager != null)
        {
            SendStateToAll();
        }
    }

    private sealed class Measurer : MonoBehaviour;

    // The host sends the channel every few seconds, which also puts a player who drifted back in step.
    private sealed class Runner : MonoBehaviour
    {
        private float untilSync = SyncEvery;

        private void Update()
        {
            untilSync -= Time.unscaledDeltaTime;
            if (untilSync > 0)
            {
                return;
            }
            untilSync = SyncEvery;
            NetworkManager network = NetworkManager.Singleton;
            if (network != null && network.IsServer && network.CustomMessagingManager != null && network.ConnectedClientsIds.Count > 1
                && FindObjectOfType<TVScript>() is { tvOn: true })
            {
                SendStateToAll();
            }
        }
    }
}
