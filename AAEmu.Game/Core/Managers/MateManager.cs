﻿using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Mate;
using AAEmu.Game.Models.Game.Skills.Buffs;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Utils.DB;

using NLog;

namespace AAEmu.Game.Core.Managers;

public class MateManager : Singleton<MateManager>
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();
    private Regex _nameRegex;

    private Dictionary<uint, List<uint>> _npcMountSkills;
    //private Dictionary<uint, NpcMountSkills> _npcMountSkills;
    private Dictionary<uint, MountSkills> _mountSkills;
    private Dictionary<uint, MountAttachedSkills> _mountAttachedSkills;
    private Dictionary<uint, List<Mate>> _activeMates; // ownerObjId, Mounts

    public List<Mate> GetActiveMates(uint ownerObjId)
    {
        return _activeMates.TryGetValue(ownerObjId, out var mates) ? mates : null;
    }

    public Mate GetActiveMateByTlId(uint ownerObjId, uint tlId)
    {
        var mates = GetActiveMates(ownerObjId);
        return mates?.FirstOrDefault(mate => mate.TlId == tlId);
    }

    public Mate GetActiveMateByMateObjId(uint ownerObjId, uint mateObjId)
    {
        var mates = GetActiveMates(ownerObjId);
        return mates?.FirstOrDefault(mate => mate.ObjId == mateObjId);
    }

    public Mate GetIsMounted(uint objId, out AttachPointKind attachPoint)
    {
        attachPoint = AttachPointKind.System;
        var mates = GetActiveMates(objId);
        if (mates == null) { return null; }
        foreach (var mate in mates)
        {
            foreach (var ati in mate.Passengers)
            {
                if (ati.Value.ObjId != objId) { continue; }
                attachPoint = ati.Key;
                return mate;
            }
        }

        return null;
    }

    public void ChangeStateMate(GameConnection connection, uint tlId, byte newState)
    {
        var (owner, mateInfo) = GetMateInfoByTlId(connection, tlId);
        if (mateInfo?.TlId != tlId) return;

        mateInfo.UserState = newState; // TODO - Maybe verify range
        //owner.BroadcastPacket(new SCMateStatePacket(), );
    }

    public void ChangeTargetMate(GameConnection connection, uint tlId, uint objId)
    {
        var (owner, mateInfo) = GetMateInfoByTlId(connection, tlId);
        if (mateInfo == null) return;
        mateInfo.CurrentTarget = objId > 0 ? WorldManager.Instance.GetUnit(objId) : null;
        owner.BroadcastPacket(new SCTargetChangedPacket(mateInfo.ObjId, mateInfo.CurrentTarget?.ObjId ?? 0), true);

        Logger.Debug("ChangeTargetMate. tlId: {0}, objId: {1}, targetObjId: {2}", mateInfo.TlId, mateInfo.ObjId, objId);
    }

    private (Character, Mate) GetMateInfoByTlId(GameConnection connection, uint tlId)
    {
        var owner = connection.ActiveChar;
        var mateInfo = GetActiveMateByTlId(owner.ObjId, tlId);
        return (owner, mateInfo);
    }

    public Mate RenameMount(GameConnection connection, uint tlId, string newName)
    {
        var (owner, mateInfo) = GetMateInfoByTlId(connection, tlId);
        if (string.IsNullOrWhiteSpace(newName) || newName.Length == 0 || !_nameRegex.IsMatch(newName)) return null;
        if (mateInfo?.TlId != tlId) return null;
        mateInfo.Name = newName.FirstCharToUpper();
        owner.BroadcastPacket(new SCUnitNameChangedPacket(mateInfo.ObjId, newName), true);
        return mateInfo;
    }

    public void MountMate(GameConnection connection, uint tlId, AttachPointKind attachPoint, AttachUnitReason reason)
    {
        var (owner, mateInfo) = GetMateInfoByTlId(connection, tlId);
        if (mateInfo == null) return;

        // Request seat position
        if (mateInfo.Passengers.TryGetValue(attachPoint, out var seatInfo))
        {
            // If first seat, check if it's the owner
            if (attachPoint == AttachPointKind.Driver && mateInfo.OwnerObjId != owner.ObjId)
            {
                Logger.Warn("MountMate. Non-owner {0} ({1}) tried to take the first seat on mount {2} ({3})", owner.Name, owner.ObjId, mateInfo.Name, mateInfo.ObjId);
                return;
            }

            // Check if seat is empty
            if (seatInfo.ObjId == 0)
            {
                owner.BroadcastPacket(new SCUnitAttachedPacket(owner.ObjId, attachPoint, reason, mateInfo.ObjId), true);
                seatInfo.ObjId = owner.ObjId;
                seatInfo.Reason = reason;

                owner.Transform.Parent = mateInfo.Transform;
                owner.Transform.Local.SetPosition(0, 0, 0); // correct the position of the character
                owner.IsRiding = true;
                owner.AttachedPoint = attachPoint;

                owner.IsVisible = true; // When we're on a horse, you can see us
            }
        }
        else
        {
            Logger.Warn("MountMate. Player {0} ({1}) tried to take a invalid seat {4} on mount {2} ({3})", owner.Name, owner.ObjId, mateInfo.Name, mateInfo.ObjId, attachPoint);
            return;
        }

        owner.Buffs.TriggerRemoveOn(BuffRemoveOn.Mount);
        Logger.Debug("MountMate. mountTlId: {0}, attachPoint: {1}, reason: {2}, seats: {3}", mateInfo.TlId, attachPoint, reason, string.Join(", ", mateInfo.Passengers.Values.ToList()));
    }

    public void UnMountMate(Character character, uint tlId, AttachPointKind attachPoint, AttachUnitReason reason)
    {
        var (owner, mateInfo) = GetMateInfoByTlId(character.Connection, tlId);
        if (mateInfo == null) return;

        mateInfo.StopUpdateXp();

        // Request seat position
        Character targetObj = null;
        if (mateInfo.Passengers.TryGetValue(attachPoint, out var seatInfo))
        {
            // Check if seat is taken by player
            if (seatInfo.ObjId != 0)
            {
                targetObj = WorldManager.Instance.GetCharacterByObjId(seatInfo.ObjId);
                seatInfo.ObjId = 0;
                seatInfo.Reason = 0;
            }
        }
        else
            targetObj = owner;

        if (targetObj != null)
        {
            targetObj.Transform.Parent = null;
            targetObj.SetPosition(mateInfo.Transform.World.Position.X, mateInfo.Transform.World.Position.Y, mateInfo.Transform.World.Position.Z, mateInfo.Transform.World.Rotation.X, mateInfo.Transform.World.Rotation.Y, mateInfo.Transform.World.Rotation.Z);
            targetObj.IsRiding = false;
            targetObj.AttachedPoint = AttachPointKind.None;
            targetObj.BroadcastPacket(new SCUnitDetachedPacket(targetObj.ObjId, reason), true);
            targetObj.Events.OnUnmount(owner, new OnUnmountArgs { });
            mateInfo.Buffs.TriggerRemoveOn(BuffRemoveOn.Unmount);
            targetObj.Buffs.TriggerRemoveOn(BuffRemoveOn.Unmount);
            Logger.Debug("UnMountMate. mountTlId: {0}, targetObjId: {1}, attachPoint: {2}, reason: {3}", mateInfo.TlId, targetObj.ObjId, attachPoint, reason);
        }
        else
            Logger.Warn("UnMountMate. No valid seat entry, mountTlId: {0}, characterObjId: {1}, attachPoint: {2}, reason: {3}", mateInfo.TlId, 0, attachPoint, reason);
    }

    public void UnMountMate(Mate mateInfo, AttachPointKind attachPoint, MatePassengerInfo seatInfo)
    {
        if (mateInfo == null) return;

        mateInfo.StopUpdateXp();

        // Request seat position
        Character targetObj = null;
        if (seatInfo != null)
        {
            // Check if seat is taken by player
            if (seatInfo.ObjId != 0)
            {
                targetObj = WorldManager.Instance.GetCharacterByObjId(seatInfo.ObjId);
                seatInfo.ObjId = 0;
                seatInfo.Reason = 0;
            }
        }

        if (targetObj != null)
        {
            targetObj.Transform.Parent = null;
            targetObj.SetPosition(mateInfo.Transform.World.Position.X, mateInfo.Transform.World.Position.Y, mateInfo.Transform.World.Position.Z, mateInfo.Transform.World.Rotation.X, mateInfo.Transform.World.Rotation.Y, mateInfo.Transform.World.Rotation.Z);
            targetObj.IsRiding = false;
            targetObj.AttachedPoint = AttachPointKind.None;
            targetObj.BroadcastPacket(new SCUnitDetachedPacket(targetObj.ObjId, seatInfo.Reason), true);
            targetObj.Events.OnUnmount(targetObj, new OnUnmountArgs { });
            mateInfo.Buffs.TriggerRemoveOn(BuffRemoveOn.Unmount);
            targetObj.Buffs.TriggerRemoveOn(BuffRemoveOn.Unmount);
            Logger.Debug("UnMountMate. mountTlId: {0}, targetObjId: {1}, attachPoint: {2}, reason: {3}", mateInfo.TlId, targetObj.ObjId, attachPoint, seatInfo.Reason);
        }
        else
            Logger.Warn("UnMountMate. No valid seat entry, mountTlId: {0}, characterObjId: {1}, attachPoint: {2}, reason: {3}", mateInfo.TlId, 0, attachPoint, seatInfo.Reason);
    }

    public void AddActiveMateAndSpawn(Character owner, Mate mate, Item item)
    {
        var mates = GetActiveMates(owner.ObjId);
        if (mates == null)
            _activeMates.Add(owner.ObjId, new List<Mate> { mate });
        else if (mates.Count < 2)
            _activeMates[owner.ObjId].Add(mate);

        owner.SendPacket(new SCItemTaskSuccessPacket(ItemTaskType.UpdateSummonMateItem, [new ItemUpdate(item)], [])); // TODO - maybe update details
        owner.SendPacket(new SCMateSpawnedPacket(mate));
        mate.Spawn();

        Logger.Debug("Mount spawned. ownerObjId: {0}, tlId: {1}, mateObjId: {2}", owner.ObjId, mate.TlId, mate.ObjId);
    }

    public void RemoveActiveMateAndDespawn(Character owner, uint tlId)
    {
        var mateInfo = GetActiveMateByTlId(owner.ObjId, tlId);
        if (mateInfo == null) return;
        if (mateInfo.TlId != tlId) return; // skip if invalid tlId

        //foreach (var ati in mateInfo.Passengers)
        //    UnMountMate(WorldManager.Instance.GetCharacterByObjId(ati.Value.ObjId), mateInfo.TlId, ati.Key, AttachUnitReason.SlaveBinding);
        foreach (var ati in mateInfo.Passengers)
            UnMountMate(mateInfo, ati.Key, ati.Value);

        mateInfo.StopUpdateXp();

        for (var i = 0; i < _activeMates[owner.ObjId].Count; i++)
        {
            if (_activeMates[owner.ObjId][i].TlId != tlId) continue;
            var am = _activeMates[owner.ObjId];
            _activeMates[owner.ObjId][i].Delete(); // despawn mate
            am.RemoveRange(i, 1);
        }

        if (_activeMates[owner.ObjId].Count == 0)
            _activeMates.Remove(owner.ObjId);

        ObjectIdManager.Instance.ReleaseId(mateInfo.ObjId);
        TlIdManager.Instance.ReleaseId(mateInfo.TlId);

        Logger.Debug($"Mount removed. OwnerObjId: {owner.ObjId}, tlId: {mateInfo.TlId}, mateObjId: {mateInfo.ObjId}");
    }

    /// <summary>
    /// Remove all mounts that are in the world and owned by character
    /// </summary>
    /// <param name="character"></param>
    public void RemoveAndDespawnAllActiveOwnedMates(Character character)
    {
        if (character == null) return;
        var mates = GetActiveMates(character.ObjId);
        if (mates == null) return;

        for (var i = 0; i < mates.Count; i++)
        {
            if (mates[i].OwnerObjId != character.ObjId) continue;
            RemoveActiveMateAndDespawn(character, mates[i].TlId);
        }
    }

    public List<uint> GetMateSkills(uint id)
    {
        foreach (var skills in _npcMountSkills)
            if (skills.Key == id)
                return skills.Value;

        return null;
    }

    /// <summary>
    /// Get the associated rider skill for a given mountSkill
    /// </summary>
    /// <param name="mateSkill">The skill the mate used</param>
    /// <param name="attachPoint">The attach point the player is currently on</param>
    /// <returns></returns>
    public uint GetMountAttachedSkills(uint mateSkill, AttachPointKind attachPoint)
    {
        var id = 0u;
        var skill = 0u;

        // Find the mountSkillId for this mate's skill
        foreach (var ms in _mountSkills)
        {
            if (ms.Value.SkillId != mateSkill)
                continue;
            id = ms.Key;
            break;
        }

        // Find the player skill based on the mountSkillId
        foreach (var mas in _mountAttachedSkills)
        {
            if (mas.Value.MountSkillId != id || mas.Value.AttachPointId != attachPoint)
                continue;
            skill = mas.Value.SkillId;
            break;
        }

        return skill;
    }

    /// <summary>
    /// Gets MountSkillId for use with Slaves
    /// </summary>
    /// <param name="slaveSkillId"></param>
    /// <returns></returns>
    public uint GetMountSkillIdForSkill(uint slaveSkillId)
    {
        foreach (var ms in _mountSkills.Values)
        {
            if (ms.SkillId == slaveSkillId)
                return ms.Id;
        }

        return 0;
    }

    public void Load()
    {
        _nameRegex = new Regex(AppConfiguration.Instance.CharacterNameRegex, RegexOptions.Compiled);
        //_npcMountSkills = new Dictionary<uint, NpcMountSkills>();
        _npcMountSkills = new Dictionary<uint, List<uint>>();
        _mountSkills = new Dictionary<uint, MountSkills>();
        _mountAttachedSkills = new Dictionary<uint, MountAttachedSkills>();
        _activeMates = new Dictionary<uint, List<Mate>>();

        #region SQLite

        using (var connection = SQLite.CreateConnection())
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT * FROM npc_mount_skills";
                command.Prepare();
                using (var reader = new SQLiteWrapperReader(command.ExecuteReader()))
                {
                    while (reader.Read())
                    {
                        var template = new NpcMountSkills();
                        //template.Id = reader.GetUInt32("id"); // there is no such field in the database for version 3.0.3.0
                        template.NpcId = reader.GetUInt32("npc_id");
                        template.MountSkillId = reader.GetUInt32("mount_skill_id");

                        if (_npcMountSkills.TryGetValue(template.NpcId, out var value))
                        {
                            if (!value.Contains(template.MountSkillId))
                            {
                                value.Add(template.MountSkillId);
                            }
                        }
                        else
                        {
                            _npcMountSkills.Add(template.NpcId, [template.MountSkillId]);
                        }
                    }
                }
            }
        }

        using (var connection = SQLite.CreateConnection())
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT * FROM mount_skills";
                command.Prepare();
                using (var reader = new SQLiteWrapperReader(command.ExecuteReader()))
                {
                    while (reader.Read())
                    {
                        var template = new MountSkills();
                        template.Id = reader.GetUInt32("id");
                        //template.Name = reader.GetString("name", ""); // there is no such field in the database for version 3.0.3.0
                        template.SkillId = reader.GetUInt32("skill_id");
                        _mountSkills.TryAdd(template.Id, template);
                    }
                }
            }
        }

        using (var connection = SQLite.CreateConnection())
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT * FROM mount_attached_skills";
                command.Prepare();
                using (var reader = new SQLiteWrapperReader(command.ExecuteReader()))
                {
                    while (reader.Read())
                    {
                        var template = new MountAttachedSkills();
                        template.Id = reader.GetUInt32("id");
                        template.MountSkillId = reader.GetUInt32("mount_skill_id");
                        template.AttachPointId = (AttachPointKind)reader.GetUInt32("attach_point_id");
                        template.SkillId = reader.GetUInt32("skill_id");
                        _mountAttachedSkills.TryAdd(template.Id, template);
                    }
                }
            }
        }

        #endregion
    }
}
