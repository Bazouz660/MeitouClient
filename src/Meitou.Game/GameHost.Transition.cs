using System.Diagnostics;

namespace Meitou.Game;

/// <summary>
/// Loads between scenes (a new game, a loaded save, the main menu) under the loading screen (docs/engine.md "Loading"): a load is a list of stages that run on this
/// thread at frame boundaries, one per frame, each after a covered frame was drawn; a stage may hand back work to wait for (a worker's read, the navmesh), and the
/// frames go on drawing the loading screen while it runs. After the last stage, and after the start, the world is drawn under the loading screen until its frames run
/// smoothly (the first frames of a new place build pipelines and stream its terrain and objects), so play starts on a smooth picture.
/// </summary>
sealed partial class GameHost
{
    sealed class SceneLoad
    {
        public readonly Queue<(string Step, Func<Task?> Run, bool DuringAscent)> Stages = new();
        public Task? Waiting;
        /// <summary>A covered frame was drawn since the last stage ran: the next one may run (it may hold the thread).</summary>
        public bool Shown;
        public readonly Stopwatch Clock = Stopwatch.StartNew();
    }

    /// <summary>A stage found the load cannot go on; the message is shown, the world as it was stays.</summary>
    sealed class SceneLoadFailed(string message) : Exception(message);

    SceneLoad? sceneLoad;
    SkyArrival? skyArrival;
    float? presentationHour;
    bool warming;
    double smoothSeconds;
    double warmSeconds;
    double readySeconds;
    double nextWarmReport;
    /// <summary>Seconds of the warm-up the VRAM guard has held its pressure without a break.</summary>
    double guardSeconds;
    bool titleWarmFinalized;
    /// <summary>After content is ready, gameplay's smooth-frame target and run, and the pacing limit (title too); the title requires <see cref="TitleSmoothRun"/>.</summary>
    const double SmoothFrame = 0.04, SmoothRun = 0.5, WarmLimit = 5;
    const double TitleSmoothRun = 1;
    /// <summary>Seconds of the guard's pressure after which the content it holds back is not waited for: nothing new loads until the use falls
    /// (VramGuard), so on a card short of memory it may never come (as <c>WorldObjectRenderer.Settle</c>).</summary>
    const double GuardWait = 5;

    /// <summary>Loading, warm-up, arrival or the loading overlay's final reveal still blocks input.</summary>
    bool Covered => sceneLoad is not null || warming || skyArrival is not null || loading is { Exiting: true };

    static (string Step, Func<Task?> Run, bool DuringAscent) Stage(string step, Func<Task?> run, bool duringAscent = false) => (step, run, duringAscent || step == "read");

    void StartSceneLoad(bool mainMenuTransition, params (string Step, Func<Task?> Run, bool DuringAscent)[] stages)
    {
        if (sceneLoad is not null) return;
        sceneLoad = new();
        if (player.UiMode == Meitou.Data.Ui.UiMode.Meitou && (mainMenuTransition ? config.MainMenuTransitions : config.InGameTransitions))
            skyArrival = new(camera, presentationHour ?? (TitleVisible ? TitleHour : (float)session.Clock.HourOfDay),
                mainMenuTransition ? config.MainMenuTransitionSpeed : config.InGameTransitionSpeed);
        foreach (var stage in stages) sceneLoad.Stages.Enqueue(stage);
        loading?.Restart();
        loadingSteps = SceneSteps;
        SetLoadingStep(stages.Length > 0 ? stages[0].Step : "build");
    }

    /// <summary>Once a frame, between ticks: the next stage of the running load when its work is done and a covered frame was drawn.</summary>
    void StepSceneLoad()
    {
        if (sceneLoad is not { } load || !load.Shown || load.Waiting is { IsCompleted: false }) return;
        // Reading a save can overlap the ascent; world mutations wait until the ground is out of view.
        if (skyArrival is { AtStars: false } && load.Stages.TryPeek(out var next) && !next.DuringAscent) return;
        try
        {
            if (load.Waiting is { IsFaulted: true } failed) throw failed.Exception!.GetBaseException();
            if (!load.Stages.TryDequeue(out var stage))
            {
                sceneLoad = null;
                ReportLoading($"scene loaded in {load.Clock.Elapsed.TotalSeconds:0.0} s");
                BeginWarm();
                return;
            }
            SetLoadingStep(stage.Step);
            load.Waiting = stage.Run();
            load.Shown = false;
        }
        catch (Exception e)
        {
            sceneLoad = null;
            skyArrival = null;
            presentationHour = null;
            hostPending = null;   // the game to host did not come
            if (e is not SceneLoadFailed) Console.Error.WriteLine($"scene     load failed: {e}");
            player.Notice(e is SceneLoadFailed ? e.Message : $"Load failed: {e.Message}", true);
        }
    }

    /// <summary>The world is drawn under the loading screen until its frames run smoothly.</summary>
    void BeginWarm()
    {
        if (loading is null) { skyArrival?.Arrive(); return; }
        (warming, smoothSeconds, warmSeconds, readySeconds, guardSeconds) = (true, 0, 0, 0, 0);
        nextWarmReport = 5;
        titleWarmFinalized = false;
        SetLoadingStep("view");
    }

    /// <summary>After a covered frame of <paramref name="seconds"/>: the warm-up ends after <see cref="SmoothRun"/> seconds of smooth frames in a row.</summary>
    void CoveredFrame(double seconds)
    {
        if (sceneLoad is { } load) load.Shown = true;
        // The frames under the cover count for the loading report (LoadingFrame counts the start's).
        loadingFrames++;
        if (seconds * 1000 > longestGap.Ms) longestGap = (seconds * 1000, currentStep);
        if (!warming) return;
        warmSeconds += seconds;
        // A ready character mesh alone is insufficient: finish terrain, scenery and deferred textures too.
        // Content readiness has no pacing timeout that can expose missing NPCs or white stand-ins.
        // A client's world is ready once a host snapshot of it is on show (a replica has no population of its own).
        bool populationReady = TitleVisible || (session.Mode == Meitou.Engine.SessionMode.Replica ? netClient?.HasPresented == true : session.World.PreparePopulation(session.Camera.Current.Target));
        bool charactersReady = gpu.Characters is not { } characters || !characters.ContentPending &&
            session.CurrentSnapshot.Characters.All(c => c.Appearance is null || characters.IsPrepared(c.Appearance));
        bool sceneReady = TitleVisible ? TitleSceneReady : populationReady && charactersReady &&
            gpu.Streamer is not { Idle: false } && !(gpu.Objects?.Pending > 0) && !(gpu.Foliage?.Pending > 0);
        guardSeconds = gpu.Guard is { Pressure: true } ? guardSeconds + seconds : 0;
        if (!sceneReady && guardSeconds >= GuardWait && (TitleVisible || populationReady))
        {
            sceneReady = true;
            if (guardSeconds - seconds < GuardWait)
                Console.WriteLine($"loading   stopped waiting for content held back by memory pressure (VramGuard, {gpu.Guard!.Fraction * 100:0}% of the {gpu.Guard.BudgetBytes / 1048576} MB budget)");
        }
        if (pendingCreation is not null && !TitleVisible && charactersReady)
        {
            // A new game shows the character editor before the world: the loading screen lifts once the squad is drawable, no sky arrival, and the world's own warm-up
            // runs when the editor closes (ResumeAfterCreation).
            (warming, skyArrival, presentationHour) = (false, null, null);
            loading?.Finish();
            ReportLoading($"the squad was ready in {warmSeconds:0.0} s; the editor opens first");
            return;
        }
        if (!sceneReady)
        {
            smoothSeconds = readySeconds = 0;
            if (warmSeconds >= nextWarmReport)
            {
                nextWarmReport = warmSeconds + 5;
                Console.WriteLine($"loading   {(TitleVisible ? "menu" : "game")} still preparing: terrain {(gpu.Streamer?.Idle == false ? "pending" : "ready")}, objects {gpu.Objects?.Pending ?? 0}{(gpu.Objects is { Pending: > 0 } po ? $" ({po.PendingDetail})" : "")}, foliage {gpu.Foliage?.Pending ?? 0}{(gpu.Foliage is { Pending: > 0 } pf ? $" ({pf.PendingDetail})" : "")}, population {(populationReady ? "ready" : "pending")}, characters {((TitleVisible ? TitleCharactersReady : charactersReady) ? "ready" : "pending")}");
            }
            return;
        }
        if (TitleVisible && !titleWarmFinalized && loadingDisplay is { } display)
        {
            // Streaming allocates after Boot's collection: retire its uploads and compact before exposing the menu.
            titleWarmFinalized = true;
            Meitou.Rendering.WorldFrame.FinishLoading(display.Context);
            smoothSeconds = 0;
            return;
        }
        readySeconds += seconds;
        smoothSeconds = seconds < SmoothFrame ? smoothSeconds + seconds : 0;
        // A machine that cannot draw the view at the smooth pace is let in after WarmLimit seconds of ready content, on the title as in play.
        if ((TitleVisible ? smoothSeconds < TitleSmoothRun : smoothSeconds < SmoothRun) && readySeconds < WarmLimit) return;
        warming = false;
        loading?.Finish();
        skyArrival?.Arrive();
        ReportLoading($"the view warmed up in {warmSeconds:0.0} s");
    }

    void AdvanceSkyArrival(double seconds)
    {
        if (skyArrival is not { } arrival) return;
        arrival.Advance(seconds);
        if (!arrival.Finished) return;
        skyArrival = null;
        presentationHour = null;
    }

    void ApplySkyArrival()
    {
        presentationHour = skyArrival?.Apply(camera, TitleVisible ? TitleHour : (float)session.Clock.HourOfDay, warming);
    }

    /// <summary>The host's side of a new world: the camera focus sent again, the first tick (it publishes the snapshot), the draw list and the mouse state dropped, the menus closed.</summary>
    void SettleNewWorld()
    {
        sentFocus = false;
        SendFocus();
        if (session.Mode == Meitou.Engine.SessionMode.Local) session.World.RunTick();   // a replica never ticks: the host's snapshot is on show
        indexedFor = null;
        previousById.Clear();
        player.Reset();
        player.CloseFrontend();
    }
}
