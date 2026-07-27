using System.Linq;
using DV.Utils;
using UnityEngine;
using JetBrains.Annotations;
using Multiplayer.Networking.Packets.Clientbound.Train;
using Multiplayer.Utils;
using Multiplayer.Networking.Data.Train;

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
        Trainset set = Trainset.allSets.Find(set => set.firstCar.GetNetId() == packet.FirstNetId || set.lastCar.GetNetId() == packet.FirstNetId ||
                                                    set.firstCar.GetNetId() == packet.LastNetId || set.lastCar.GetNetId() == packet.LastNetId);

        if (set == null)
        {
            Multiplayer.LogWarning($"Received {nameof(ClientboundTrainsetPhysicsPacket)} for unknown trainset with FirstNetId: {packet.FirstNetId} and LastNetId: {packet.LastNetId}");
            return;
        }

        if (set.cars.Count != packet.TrainsetParts.Length)
        {
            //log the discrepancies
            //Multiplayer.LogWarning(
            //    $"Received {nameof(ClientboundTrainsetPhysicsPacket)} for trainset with FirstNetId: {packet.FirstNetId} and LastNetId: {packet.LastNetId} with {packet.TrainsetParts.Length} parts, but trainset has {set.cars.Count} parts");

            for (int i = 0; i < packet.TrainsetParts.Length; i++)
            {
                if (NetworkedTrainCar.TryGet(packet.TrainsetParts[i].NetId ,out NetworkedTrainCar networkedTrainCar))
                {
                    //Multiplayer.LogDebug(()=>$"Applying TrainPhysicsUpdate to {packet.TrainsetParts[i].NetId}");
                    networkedTrainCar.Client_ReceiveTrainPhysicsUpdate(in packet.TrainsetParts[i], packet.Tick);
                }
                else
                {
                    Multiplayer.LogWarning($"Unable to apply TrainPhysicsUpdate to {packet.TrainsetParts[i].NetId}, NetworkedTrainCar not found!");
                }
            }
            return;
        }

        //Check direction of trainset vs packet
        if(set.firstCar.GetNetId() == packet.LastNetId)
            packet.TrainsetParts = packet.TrainsetParts.Reverse().ToArray();

        //Multiplayer.Log($"Client_HandleTrainsetPhysicsUpdate({set.firstCar.ID}):, tick: {packet.Tick}");

        for (int i = 0; i < packet.TrainsetParts.Length; i++)
        {
            if(set.cars[i].TryNetworked(out NetworkedTrainCar networkedTrainCar))
                networkedTrainCar.Client_ReceiveTrainPhysicsUpdate(in packet.TrainsetParts[i], packet.Tick);
            else
                Multiplayer.LogWarning($"Unable to apply TrainPhysicsUpdate to TrainSet with FirstNetId: {packet.FirstNetId}, NetworkedTrainCar not found!");
        }
    }
     
    #endregion

    [UsedImplicitly]
    public new static string AllowAutoCreate()
    {
        return $"[{nameof(NetworkTrainsetWatcher)}]";
    }
}
