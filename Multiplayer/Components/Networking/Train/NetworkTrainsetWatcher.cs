using DV.Utils;
using JetBrains.Annotations;
using Multiplayer.Networking.Data.Train;
using Multiplayer.Networking.Packets.Clientbound.Train;
using Multiplayer.Utils;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Multiplayer.Components.Networking.Train;

public class NetworkTrainsetWatcher : SingletonBehaviour<NetworkTrainsetWatcher>
{
    private ClientboundTrainsetPhysicsPacket cachedSendPacket;

    const float DESIRED_FULL_SYNC_INTERVAL = 2f; // in seconds
    const int MAX_UNSYNC_TICKS = (int)(NetworkLifecycle.TICK_RATE * DESIRED_FULL_SYNC_INTERVAL);
    // A stationary trainset's state does not change between syncs, so its periodic
    // re-broadcast serves only to heal lost packets. Parked sets re-sync on this longer
    // interval instead of every 2s; with a large parked fleet the 2s cadence dominates
    // host send volume. Track changes under a parked set sync immediately instead of
    // waiting for the timer.
    const float STATIONARY_FULL_SYNC_INTERVAL = 30f; // in seconds
    const int STATIONARY_MAX_UNSYNC_TICKS = (int)(NetworkLifecycle.TICK_RATE * STATIONARY_FULL_SYNC_INTERVAL);
    public const float VELOCITY_THRESHOLD = 0.01f;
    public const float MAX_POSITION_DELTA = 2f; //if the delta is greater than this we will do a hard correction

    // Mismatch, unknown-set and unable-to-apply warnings fired at packet rate (261k unable-to-apply
    // and 72k unknown-set lines in a single host session). Log the first occurrence then 1-in-N with
    // a running count, and resync a desynced set at most once per cooldown window.
    private const uint MISMATCH_RESYNC_COOLDOWN_TICKS = 120; // ~5s at TICK_RATE
    private const int SUPPRESSED_LOG_INTERVAL = 1000;
    private readonly Dictionary<ushort, uint> client_lastMismatchResyncTick = new();
    private long mismatchLogCount;
    private long unknownSetLogCount;
    private long unableToApplyLogCount;

    // Sync volume heartbeat so a session log shows send rates without a profiler.
    private int heartbeatPackets;
    private int heartbeatFullSyncs;
    private float heartbeatNextLogTime;

    protected override void Awake()
    {
        base.Awake();
        if (!NetworkLifecycle.Instance.IsHost())
            return;
        cachedSendPacket = new ClientboundTrainsetPhysicsPacket();
        NetworkLifecycle.Instance.OnTick += Server_OnTick;
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (UnloadWatcher.isQuitting)
            return;
        if (NetworkLifecycle.Instance.IsHost())
            NetworkLifecycle.Instance.OnTick -= Server_OnTick;
    }

    #region Server

    private void Server_OnTick(uint tick)
    {

        cachedSendPacket.Tick = tick;
        foreach (Trainset set in Trainset.allSets)
        {
            if (UnloadWatcher.isUnloading || UnloadWatcher.isQuitting)
                return;

            if (set != null && set.cars != null)
                Server_TickSet(set, tick);
            else
                Multiplayer.LogWarning($"Server_OnTick(): Trainset or cars are null. Set Id: {set?.id}, Cars: {set?.cars?.Count}");
        }

        if (Time.unscaledTime >= heartbeatNextLogTime)
        {
            if (heartbeatNextLogTime > 0f)
                Multiplayer.Log($"Trainset sync heartbeat: {Trainset.allSets.Count} sets, {heartbeatPackets} physics packets, {heartbeatFullSyncs} full syncs in last 60s");
            heartbeatPackets = 0;
            heartbeatFullSyncs = 0;
            heartbeatNextLogTime = Time.unscaledTime + 60f;
        }
    }

    private void Server_TickSet(Trainset set, uint tick)
    {
        bool anyCarMoving = false;
        bool anyCarTeleporting = false;
        bool anyTracksDirty = false;
        uint maxTicksSinceSync = 0;

        if (UnloadWatcher.isUnloading || UnloadWatcher.isQuitting)
            return;

        if (set == null)
        {
            Multiplayer.LogWarning("Server_TickSet() called with null Trainset!");
            return;
        }

        if (set.firstCar == null || set.lastCar == null)
        {
            Multiplayer.LogWarning($"Trainset {set?.id} has null end cars! firstCar: {set?.firstCar != null}, lastCar: {set?.lastCar != null}");
            return;
        }
        cachedSendPacket.FirstNetId = set.firstCar.GetNetId();
        cachedSendPacket.LastNetId = set.lastCar.GetNetId();

        // Car may not be initialised, missing a valid NetID
        if (cachedSendPacket.FirstNetId == 0 || cachedSendPacket.LastNetId == 0)
            return;

        foreach (TrainCar trainCar in set.cars)
        {
            if (trainCar == null || trainCar.gameObject == null || !trainCar.gameObject.activeSelf)
            {
                Multiplayer.LogError($"Trainset {set?.id} ({set.firstCar?.GetNetId()}) has a null or inactive car! trainCar: {trainCar != null}, gameObject: {trainCar?.gameObject != null}, active: {trainCar?.gameObject?.activeSelf}");
                return;
            }

            // Check if Bogies array is valid before proceeding
            if (trainCar.Bogies == null || trainCar.Bogies.Length < 2)
            {
                Multiplayer.LogError($"TrainCar {trainCar?.ID} in set {set?.id} Bogies array are null: {trainCar.Bogies == null}, Length: {trainCar.Bogies?.Length}");
                return;
            }

            if (trainCar.Bogies[0] == null || trainCar.Bogies[1] == null)
            {
                Multiplayer.LogError($"TrainCar {trainCar?.ID} in set {set?.id} is missing Bogies! Bogie[0] is null: {trainCar.Bogies[0] == null}, Bogie[1] is null: {trainCar.Bogies[1] == null}");
                return;
            }

            // If we can locate the networked car, we'll add to the ticks counter and check if any tracks are dirty
            if (NetworkedTrainCar.TryGetFromTrainCar(trainCar, out NetworkedTrainCar netTC) && netTC != null)
            {
                if (netTC.DoNotUpdate)
                    return;

                if (netTC.TicksSinceSync > maxTicksSinceSync)
                    maxTicksSinceSync = netTC.TicksSinceSync; //whether this forces a sync depends on the set's stationary state, decided after the loop
                anyTracksDirty |= netTC.BogieTracksDirty;
            }
            else
            {
                Multiplayer.LogError($"NetworkedTrainCar not found for TrainCar {trainCar?.ID} in set {set?.id} ({set.firstCar?.GetNetId()})");
                return;
            }

            if (trainCar.derailed)
            {
                if (trainCar?.rb == null)
                {
                    Multiplayer.LogError($"Rigid body not found for TrainCar {trainCar?.ID} in set {set?.id} ({set.firstCar?.GetNetId()})");
                    return;
                }

                // Check if derailed car is actually moving
                float velocityMagnitude = trainCar.rb.velocity.magnitude;
                if (velocityMagnitude > VELOCITY_THRESHOLD)
                {
                    anyCarMoving = true;
                }
            }
            else if (!trainCar.isStationary)
                anyCarMoving = true;

            anyCarTeleporting = trainCar.IsTeleporting;
            if (anyCarTeleporting)
                Multiplayer.LogDebug(() => $"Server_TickSet() {trainCar?.ID} in set {set.id} is teleporting");

            // We can finish checking early if we have a car moving or teleporting; a stationary
            // set needs the full walk so the sync decision below sees every car's tick count
            // and dirty-track flag
            if (anyCarMoving || anyCarTeleporting)
            {
                //Multiplayer.LogDebug(() => $"Server_TickSet() TrainCar {trainCar.ID} ({netTC?.NetId}) from set: {cachedSendPacket.FirstNetId} is moving or due for sync! stationary: {trainCar.isStationary}, RB velocity: {trainCar.rb.velocity} {trainCar.rb.velocity.magnitude}, tracks dirty: {netTC?.BogieTracksDirty}");
                break;
            }
        }

        // Moving sets keep the 2s full-sync cadence; stationary sets stretch to the long
        // interval and sync immediately when a track under them changed
        bool maxTicksReached = anyCarMoving
            ? maxTicksSinceSync >= MAX_UNSYNC_TICKS
            : maxTicksSinceSync >= STATIONARY_MAX_UNSYNC_TICKS || anyTracksDirty;

        // If any car is dirty or exceeded its max ticks we will re-sync the entire train
        if (!anyCarMoving && !maxTicksReached || anyCarTeleporting)
            return;

        TrainsetMovementPart[] trainsetParts = new TrainsetMovementPart[set.cars.Count];

        for (int i = 0; i < set.cars.Count; i++)
        {
            TrainCar trainCar = set.cars[i];
            if (!trainCar.TryNetworked(out NetworkedTrainCar networkedTrainCar))
            {
                Multiplayer.LogDebug(() => $"TrainCar {trainCar?.ID} is not networked! Is active? {trainCar?.gameObject?.activeInHierarchy}");
                continue;
            }

            if (trainCar.derailed)
            {
                trainsetParts[i] = new TrainsetMovementPart(networkedTrainCar.NetId, RigidbodySnapshot.From(trainCar.rb));
                // A rigidbody snapshot is a full state send for a derailed car. Without this
                // reset its tick counter grows forever, which kept any set containing a
                // stationary wreck broadcasting at full tick rate for the rest of the session
                if (maxTicksReached)
                    networkedTrainCar.TicksSinceSync = 0;
            }
            else
            {
                Vector3? position = null;
                Quaternion? rotation = null;

                // Have we exceeded the max ticks?
                if (maxTicksReached)
                {
                    position = trainCar.transform.position - WorldMover.currentMove;
                    rotation = trainCar.transform.rotation;

                    networkedTrainCar.TicksSinceSync = 0;
                }

                trainsetParts[i] = new TrainsetMovementPart(
                    networkedTrainCar.NetId,
                    trainCar.GetForwardSpeed(),
                    trainCar.stress.slowBuildUpStress,
                    BogieData.FromBogie(trainCar.Bogies[0]),
                    BogieData.FromBogie(trainCar.Bogies[1]),
                    position,   //only used in full sync
                    rotation    //only used in full sync
                );
            }

            //reset this car's states
            networkedTrainCar.BogieTracksDirty = false;
        }

        cachedSendPacket.TrainsetParts = trainsetParts;
        heartbeatPackets++;
        if (maxTicksReached)
            heartbeatFullSyncs++;
        NetworkLifecycle.Instance.Server.SendTrainsetPhysicsUpdate(cachedSendPacket, anyTracksDirty);
    }
    #endregion

    #region Client

    public void Client_HandleTrainsetPhysicsUpdate(ClientboundTrainsetPhysicsPacket packet)
    {
        Trainset set = Trainset.allSets.Find
        (
            set =>
            set.firstCar.GetNetId() == packet.FirstNetId ||
            set.lastCar.GetNetId() == packet.FirstNetId ||
            set.firstCar.GetNetId() == packet.LastNetId ||
            set.lastCar.GetNetId() == packet.LastNetId
        );

        if (set == null)
        {
            if (unknownSetLogCount++ % SUPPRESSED_LOG_INTERVAL == 0)
                Multiplayer.LogWarning($"Received {nameof(ClientboundTrainsetPhysicsPacket)} for unknown trainset with FirstNetId: {packet.FirstNetId} and LastNetId: {packet.LastNetId} (occurrence #{unknownSetLogCount})");
            return;
        }

        // We have missing cars - TODO: resolve
        if (set.cars.Count != packet.TrainsetParts.Length)
        {
            //log the discrepancies
            //Multiplayer.LogWarning(
            //    $"Received {nameof(ClientboundTrainsetPhysicsPacket)} for trainset with FirstNetId: {packet.FirstNetId} and LastNetId: {packet.LastNetId} with {packet.TrainsetParts.Length} parts, but trainset has {set.cars.Count} parts");

            // Composition desync never healed on its own: the warning above was silenced and the
            // packet is best-efforted forever. Log it at a survivable rate, and ask the server to
            // re-send state for the boundary car so coupling info comes back, at most once per
            // cooldown window per set.
            if (mismatchLogCount++ % SUPPRESSED_LOG_INTERVAL == 0)
                Multiplayer.LogWarning($"Received {nameof(ClientboundTrainsetPhysicsPacket)} for trainset with FirstNetId: {packet.FirstNetId} and LastNetId: {packet.LastNetId} with {packet.TrainsetParts.Length} parts, but trainset has {set.cars.Count} parts (occurrence #{mismatchLogCount})");

            ushort mismatchNetId = (ushort)packet.FirstNetId;
            if (!client_lastMismatchResyncTick.TryGetValue(mismatchNetId, out uint lastMismatchResync)
                || packet.Tick > lastMismatchResync + MISMATCH_RESYNC_COOLDOWN_TICKS
                || lastMismatchResync > packet.Tick)
            {
                client_lastMismatchResyncTick[mismatchNetId] = packet.Tick;
                NetworkLifecycle.Instance?.Client?.SendTrainSyncRequest(mismatchNetId);
            }

            for (int i = 0; i < packet.TrainsetParts.Length; i++)
            {
                if (NetworkedTrainCar.TryGet(packet.TrainsetParts[i].NetId, out NetworkedTrainCar networkedTrainCar))
                {
                    //Multiplayer.LogDebug(()=>$"Applying TrainPhysicsUpdate to {packet.TrainsetParts[i].NetId}");
                    networkedTrainCar.Client_ReceiveTrainPhysicsUpdate(in packet.TrainsetParts[i], packet.Tick);
                }
                else
                {
                    if (unableToApplyLogCount++ % SUPPRESSED_LOG_INTERVAL == 0)
                        Multiplayer.LogWarning($"Unable to apply TrainPhysicsUpdate to {packet.TrainsetParts[i].NetId}, NetworkedTrainCar not found! (occurrence #{unableToApplyLogCount})");
                }
            }
            return;
        }

        //Check direction of trainset vs packet
        if (set.firstCar.GetNetId() == packet.LastNetId)
            packet.TrainsetParts = packet.TrainsetParts.Reverse().ToArray();

        // Check if any of the cars have exceeded the threshold for a hard sync
        Dictionary<NetworkedTrainCar, TrainsetMovementPart> networkedCars = new(set.cars.Count);
        bool hardSyncRequired = false;
        bool missingCars = false;
        for (int i = 0; i < packet.TrainsetParts.Length; i++)
        {
            if (NetworkedTrainCar.TryGet(packet.TrainsetParts[i].NetId, out NetworkedTrainCar networkedTrainCar))
            {
                networkedCars.Add(networkedTrainCar, packet.TrainsetParts[i]);

                bool thresholdExceeded = networkedTrainCar.Client_CheckThreshold(in packet.TrainsetParts[i], packet.Tick);

                hardSyncRequired |= thresholdExceeded;

                //if (thresholdExceeded)
                //    Multiplayer.LogDebug(() => $"Client_ReceiveTrainPhysicsUpdate() First: {packet.FirstNetId}, Last: {packet.LastNetId}, Count: {packet.TrainsetParts.Length}");
            }
            else
            {
                if (unableToApplyLogCount++ % SUPPRESSED_LOG_INTERVAL == 0)
                    Multiplayer.LogWarning($"Unable to apply TrainPhysicsUpdate to {packet.TrainsetParts[i].NetId}, NetworkedTrainCar not found! (occurrence #{unableToApplyLogCount})");
                missingCars = true;
            }
        }

        if (hardSyncRequired)
        {
            //Multiplayer.LogDebug(() => $"Client_ReceiveTrainPhysicsUpdate() Hard sync required for trainset with FirstNetId: {packet.FirstNetId}, LastNetId: {packet.LastNetId}");

            CoroutineManager.Instance.StartCoroutine(Client_HardCorrect(networkedCars, packet.Tick));
            return;
        }

        for (int i = 0; i < packet.TrainsetParts.Length; i++)
        {
            if (set.cars[i].TryNetworked(out NetworkedTrainCar networkedTrainCar))
                networkedTrainCar.Client_ReceiveTrainPhysicsUpdate(in packet.TrainsetParts[i], packet.Tick);
            else
                if (unableToApplyLogCount++ % SUPPRESSED_LOG_INTERVAL == 0)
                    Multiplayer.LogWarning($"Unable to apply TrainPhysicsUpdate to TrainSet with FirstNetId: {packet.FirstNetId}, NetworkedTrainCar not found! (occurrence #{unableToApplyLogCount})");
        }
    }

    private IEnumerator Client_HardCorrect(Dictionary<NetworkedTrainCar, TrainsetMovementPart> networkedCars, uint tick)
    {
        foreach (var kvp in networkedCars)
            kvp.Key.Client_BeginHardCorrection(kvp.Value, tick);

        Physics.SyncTransforms();

        yield return new WaitForFixedUpdate();

        foreach (var kvp in networkedCars)
            kvp.Key.Client_EndHardCorrection(kvp.Value, tick);
    }

    #endregion

    [UsedImplicitly]
    public new static string AllowAutoCreate()
    {
        return $"[{nameof(NetworkTrainsetWatcher)}]";
    }
}
