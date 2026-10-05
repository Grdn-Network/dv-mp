using DV.JObjectExtstensions;
using Multiplayer.Components.Networking;
using Multiplayer.Components.Networking.Train;
using Multiplayer.Components.Networking.World;
using Multiplayer.Components.SaveGame;
using Multiplayer.Networking.Data.Player;
using Multiplayer.Networking.TransportLayers;
using Multiplayer.Utils;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Multiplayer.Networking.Data;

public class ServerPlayer : IDisposable
{
    public const byte MAX_CREW_NAME_LENGTH = 6;
    #region ID Management
    private static readonly IdPool<byte> idPool = new();

    public void Dispose()
    {
        Multiplayer.LogDebug(() => $"Disposing ServerPlayer {Username} ({PlayerId})");
        if (PlayerId != 0)
        {
            idPool.ReleaseId(PlayerId);
            PlayerId = 0;
        }
    }
    #endregion

    public ITransportPeer Peer { get; private set; }
    public byte PlayerId { get; private set; }
    internal PlayerLoadingState LoadingState { get; set; } = PlayerLoadingState.None;
    public DateTime LastLogin { get; set; }
    private float PreviousPlayTime { get; set; }
    public float TotalPlaytime => PreviousPlayTime + (DateTime.UtcNow - LastLogin).Minutes;
    public string Username { get; set; }
    public string OriginalUsername { get; set; }
    public Guid Guid { get; set; }
    public string CharacterId { get; set; }
    public bool IsVR { get; }
    public uint LastHighPingTickLogged { get; set; }
    public uint LastPositionErrorTickLogged { get; set; }

    public PlayerTrackingData TrackingData { get; set; }
    public PlayerPostureFlags Posture { get; set; }        // already exists — keep
    public ushort CarId { get; set; }
    private string _crewName;
    public string CrewName
    {
        get
        {
            if (string.IsNullOrEmpty(_crewName))
                return string.Empty;
            return _crewName;
        }
        set
        {
            if (value != null)
            {
                if (value.Length > MAX_CREW_NAME_LENGTH)
                {
                    Multiplayer.LogWarning($"CrewName for player {Username} exceeds max length of {MAX_CREW_NAME_LENGTH}. Truncating.");
                    _crewName = value.Substring(0, MAX_CREW_NAME_LENGTH);
                }
                else
                {
                    _crewName = value;
                }
            }
            else
            {
                _crewName = string.Empty;
            }

            Dictionary<PlayerPreference, string> preferences = new()
            {
                { PlayerPreference.CrewName, _crewName }
            };

            NetworkLifecycle.Instance.Server.SendPlayerPreferencesUpdate(this, preferences);
        }
    }

    public string DisplayName
    {
        get
        {
            if (string.IsNullOrEmpty(CrewName))
                return Username;
            return $"[{CrewName}] {Username}";
        }
    }

    public Dictionary<NetworkedItem, uint> KnownItems { get; private set; } = new Dictionary<NetworkedItem, uint>(); //NetworkedItem, last updated tick
    public Dictionary<NetworkedItem, float> NearbyItems { get; private set; } = new Dictionary<NetworkedItem, float>(); //NetworkedItem, time since near the item
    public HashSet<ushort> OwnedItems { get; private set; } = new HashSet<ushort>();
    public StorageBase Storage { get; set; } = new StorageBase();

    private Vector3 _lastWorldPos = Vector3.zero;
    private bool _hasLastWorldPos;
    private Vector3 _lastAbsoluteWorldPosition = Vector3.zero;
    private bool _hasLastAbsoluteWorldPosition;

    public ServerPlayer(ITransportPeer peer, string username, string originalUsername, Guid guid, string characterId, bool isVr)
    {
        PlayerId = idPool.NextId;

        Peer = peer;
        LastLogin = DateTime.UtcNow;

        Username = username;
        OriginalUsername = originalUsername;
        Guid = guid;
        CharacterId = characterId;

        IsVR = isVr;
    }

    #region Positioning
    //only log a position resolution failure once every 60 seconds per player, matching the high ping throttle
    private const int POSITION_ERROR_LOG_INTERVAL = 60;

    public Vector3 RawPosition => TrackingData.Position ?? Vector3.zero;
    public float RawRotationY => TrackingData.RotationY ?? 0f;

    /// <summary>
    /// Resolves the transform of the car this player is riding.
    /// </summary>
    /// <returns>False when the player is not riding a car, or that car no longer exists.</returns>
    private bool TryGetCarTransform(out Transform carTransform)
    {
        carTransform = null;

        if (CarId == 0 || !NetworkedTrainCar.TryGet(CarId, out NetworkedTrainCar car))
            return false;

        // TryGet hands back a Unity-null component when the car has been destroyed but is still
        // registered, which happens for a frame or two after DestroyTrainCarPacket. Unity throws
        // on member access for a destroyed component, and its == null override is the only thing
        // that detects it, so test that before touching .transform.
        if (car == null)
            return false;

        carTransform = car.transform;
        return carTransform != null;
    }

    /// <summary>
    /// Logs a position resolution failure at most once per POSITION_ERROR_LOG_INTERVAL per player.
    /// </summary>
    private void LogPositionFailureThrottled(string source, Exception e)
    {
        uint tick = NetworkLifecycle.Instance?.Tick ?? 0;

        if (LastPositionErrorTickLogged != 0 && tick >= LastPositionErrorTickLogged &&
            tick - LastPositionErrorTickLogged <= NetworkLifecycle.TICK_RATE * POSITION_ERROR_LOG_INTERVAL)
            return;

        LastPositionErrorTickLogged = tick;
        Multiplayer.LogWarning($"{source}() could not resolve a position for {Username}: car {CarId} no longer exists, using last known position");

        if (e != null)
            Multiplayer.LogWarning(e.Message);
    }

    /// <summary>
    /// Gets this player's position in world space.
    /// </summary>
    /// <param name="position">
    /// The live position when it can be resolved, otherwise the last known position (or the raw
    /// tracked position when one was never resolved). Never the world origin, which would place
    /// the player at 0,0,0 for every proximity test.
    /// </param>
    /// <returns>True when the position is live, false when it is a stale estimate.</returns>
    public bool TryGetWorldPosition(out Vector3 position)
    {
        if (CarId == 0 || !NetworkedTrainCar.TryGet(CarId, out NetworkedTrainCar car))
        {
            if (CarId != 0)
                Multiplayer.LogDebug(() => $"WorldPosition() noID {Username}: CarId: {CarId}");

            position = RawPosition + WorldMover.currentMove;
            _lastWorldPos = position;
            _hasLastWorldPos = true;
            return true;
        }

        try
        {
            Transform carTransform = car == null ? null : car.transform;

            if (carTransform != null)
            {
                position = carTransform.TransformPoint(RawPosition);
                _lastWorldPos = position;
                _hasLastWorldPos = true;
                return true;
            }
        }
        catch (Exception e)
        {
            LogPositionFailureThrottled(nameof(WorldPosition), e);
            position = _hasLastWorldPos ? _lastWorldPos : RawPosition + WorldMover.currentMove;
            return false;
        }

        // The car is gone, so RawPosition is a car local offset with nothing to transform it by.
        // Fall back to the last resolved position: returning a zero vector here would put the
        // player at the world origin for every sqrMagnitude proximity test.
        LogPositionFailureThrottled(nameof(WorldPosition), null);
        position = _hasLastWorldPos ? _lastWorldPos : RawPosition + WorldMover.currentMove;
        return false;
    }

    /// <summary>
    /// Gets this player's position in absolute world space (world mover offset removed).
    /// </summary>
    /// <returns>True when the position is live, false when it is a stale estimate.</returns>
    public bool TryGetAbsoluteWorldPosition(out Vector3 position)
    {
        if (CarId == 0 || !NetworkedTrainCar.TryGet(CarId, out NetworkedTrainCar car))
        {
            if (CarId != 0)
                Multiplayer.LogDebug(() => $"AbsoluteWorldPosition() noID {Username}: CarId: {CarId}");

            position = RawPosition;
            _lastAbsoluteWorldPosition = position;
            _hasLastAbsoluteWorldPosition = true;
            return true;
        }

        try
        {
            Transform carTransform = car == null ? null : car.transform;

            if (carTransform != null)
            {
                position = carTransform.TransformPoint(RawPosition) - WorldMover.currentMove;
                _lastAbsoluteWorldPosition = position;
                _hasLastAbsoluteWorldPosition = true;
                return true;
            }
        }
        catch (Exception e)
        {
            LogPositionFailureThrottled(nameof(AbsoluteWorldPosition), e);
            position = _hasLastAbsoluteWorldPosition ? _lastAbsoluteWorldPosition : RawPosition;
            return false;
        }

        LogPositionFailureThrottled(nameof(AbsoluteWorldPosition), null);
        position = _hasLastAbsoluteWorldPosition ? _lastAbsoluteWorldPosition : RawPosition;
        return false;
    }

    public Vector3 AbsoluteWorldPosition
    {
        get
        {
            TryGetAbsoluteWorldPosition(out Vector3 position);
            return position;
        }
    }

    public Vector3 WorldPosition
    {
        get
        {
            TryGetWorldPosition(out Vector3 position);
            return position;
        }
    }

    public float WorldRotationY
    {
        get
        {
            // This getter had no exception handling at all, so a destroyed car threw straight out
            // of it rather than being caught like the position getters.
            if (!TryGetCarTransform(out Transform carTransform))
                return RawRotationY;

            return (Quaternion.Euler(0, RawRotationY, 0) * carTransform.rotation).eulerAngles.y;
        }
    }
    #endregion

    #region Item Ownership
    public bool OwnsItem(ushort itemNetId) => OwnedItems.Contains(itemNetId);

    public void AddOwnedItem(ushort itemNetId)
    {
        OwnedItems.Add(itemNetId);
        NetworkLifecycle.Instance.Server.LogDebug(() => $"Player {Username} now owns item {itemNetId}");
    }

    public void AddOwnedItems(IEnumerable<ushort> itemNetIds)
    {
        OwnedItems.UnionWith(itemNetIds);
        NetworkLifecycle.Instance.Server.LogDebug(() => $"Player {Username} batch added items: {string.Join(", ", itemNetIds)}");
    }

    public void RemoveOwnedItem(ushort itemNetId)
    {
        if (OwnedItems.Remove(itemNetId))
        {
            NetworkLifecycle.Instance.Server.LogDebug(() => $"Player {Username} no longer owns item {itemNetId}");
        }
    }

    public void ClearOwnedItems()
    {
        OwnedItems.Clear();
        NetworkLifecycle.Instance.Server.LogDebug(() => $"Cleared all owned items for player {Username}");
    }

    public bool TryGetOwnedItem(ushort itemNetId, out NetworkedItem item)
    {
        if (OwnedItems.Contains(itemNetId) && NetworkedItem.TryGet(itemNetId, out item))
        {
            return true;
        }
        item = null;
        return false;
    }
    #endregion

    public override string ToString()
    {
        return $"{PlayerId} ({Username}, {Guid.ToString()})";
    }
}
