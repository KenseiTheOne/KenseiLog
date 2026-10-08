using KenseiLog;
using UnityEngine;

/// <summary>
/// The record <see cref="PlayEntryCheck"/> looks for: one line from the earliest point of a run
/// that a project's own code reaches, which is where a startup error would come from.
/// A runtime script, because the editor assembly is not where play mode looks for these.
/// </summary>
public static class PlayEntryBoot {
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Boot() {
        Log.Info("PlayEntryCheck", "logged before the first scene loaded");
    }
}
