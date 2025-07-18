﻿using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.Automation;
using ECommons.ChatMethods;
using ECommons.CircularBuffers;
using ECommons.GameFunctions;
using ECommons.GameHelpers;
using ECommons.MathHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using System.Linq;
using UIColor = ECommons.ChatMethods.UIColor;

namespace TextAdvance.Navmesh;
public unsafe class MoveManager
{
    private MoveManager() { }

    private void Log(string message)
    {
        PluginLog.Debug($"[MoveManager] {message}");
        if (C.NavStatusChat)
        {
            ChatPrinter.PrintColored(UIColor.WarmSeaBlue, $"[TextAdvance] {message}");
        }
    }

    public void MoveToFlag()
    {
        if (!Player.Available) return;
        if (AgentMap.Instance()->IsFlagMarkerSet == false)
        {
            DuoLog.Warning($"Flag is not set");
            return;
        }
        if (AgentMap.Instance()->FlagMapMarker.TerritoryId != Svc.ClientState.TerritoryType)
        {
            DuoLog.Warning($"Flag is in different zone than current");
            return;
        }
        var m = AgentMap.Instance()->FlagMapMarker;
        var pos = P.NavmeshManager.PointOnFloor(new(m.XFloat, 1024, m.YFloat), false, 5);
        var iterations = 0;
        if (pos == null)
        {
            for (var extent = 0; extent < 100; extent += 5)
            {
                for (var i = 0; i < 1000; i += 5)
                {
                    iterations++;
                    pos ??= P.NavmeshManager.NearestPoint(new(m.XFloat, Player.Object.Position.Y + i, m.YFloat), extent, 5);
                    pos ??= P.NavmeshManager.NearestPoint(new(m.XFloat, Player.Object.Position.Y - i, m.YFloat), extent, 5);
                    if (pos != null) break;
                }
            }
        }
        if (pos == null)
        {
            DuoLog.Error($"Failed to move to flag");
            return;
        }
        this.EnqueueMoveAndInteract(new(pos.Value, 0, true), 3f);
        this.Log($"Nav to flag {pos.Value:F1}, {iterations} corrections");
    }

    public void MoveTo2DPoint(MoveData data, float distance)
    {
        var pos = P.NavmeshManager.PointOnFloor(new(data.Position.X, 1024, data.Position.Z), false, 5);
        var iterations = 0;
        if (pos == null)
        {
            for (var extent = 0; extent < 100; extent += 5)
            {
                for (var i = 0; i < 1000; i += 5)
                {
                    iterations++;
                    pos ??= P.NavmeshManager.NearestPoint(new(data.Position.X, Player.Object.Position.Y + i, data.Position.Z), extent, 5);
                    pos ??= P.NavmeshManager.NearestPoint(new(data.Position.X, Player.Object.Position.Y - i, data.Position.Z), extent, 5);
                    if (pos != null) break;
                }
            }
        }
        if (pos == null)
        {
            DuoLog.Error($"Failed to move to 2d point");
            return;
        }
        data.Position = pos.Value;
        this.EnqueueMoveAndInteract(data, distance);
        this.Log($"Nav to 2d point {pos.Value:F1}, {iterations} corrections, distance={distance:F1}");
    }
    public void MoveTo3DPoint(MoveData data, float distance)
    {
        this.EnqueueMoveAndInteract(data, distance);
        this.Log($"Nav to 3d point {data.Position:F1}, distance={distance:F1}");
    }

    public void MoveToQuest()
    {
        if (!Player.Available) return;
        //S.EntityOverlay.AutoFrame = CSFramework.Instance()->FrameCounter + 1;
        if (EzThrottler.Throttle("WarnMTQ", int.MaxValue))
        {
            //ChatPrinter.Red($"[TextAdvance] MoveToQuest function may not work correctly until complete Dalamud update");
        }
        var obj = this.GetNearestMTQObject();
        if (obj != null)
        {
            this.EnqueueMoveAndInteract(new(obj.Position, obj.DataId, false), 3f);
            this.Log($"Precise nav: {obj.Name}/{obj.DataId:X8}");
        }
        else
        {
            Utils.GetEligibleMapMarkerLocationsAsync(Callback);
            void Callback(List<Vector3> markers)
            {
                if (markers.Count > 0)
                {
                    var marker = markers.OrderBy(x => Vector3.Distance(x, Player.Object.Position)).First();
                    this.EnqueueMoveAndInteract(new(marker, 0, false), 3f);
                    this.Log($"Non-precise nav: {marker:F1}");
                }
            }
        }
    }

    private IGameObject GetNearestMTQObject(Vector3? reference = null, float? maxDistance = null)
    {
        if (!Player.Available) return null;
        if (!(C.Navmesh && P.NavmeshManager.IsReady())) return null;
        reference ??= Player.Object.Position;
        foreach (var x in Svc.Objects.OrderBy(z => Vector3.Distance(reference.Value, z.Position)))
        {
            if (maxDistance != null && Vector3.Distance(reference.Value, x.Position) > maxDistance) continue;
            if (x.IsMTQ()) return x;
        }
        return null;
    }

    public void EnqueueMoveAndInteract(MoveData data, float distance)
    {
        this.SpecialAdjust(data);
        P.NavmeshManager.Stop();
        S.EntityOverlay.TaskManager.Abort();
        /*if (Svc.Condition[ConditionFlag.InFlight])
        {
            Svc.Toasts.ShowError("[TextAdvance] Flying pathfinding is not supported");
            return;
        }*/
        if (data.Mount ?? Vector3.Distance(data.Position, Player.Object.Position) > 20f)
        {
            S.EntityOverlay.TaskManager.Enqueue(this.MountIfCan);
        }
        if (data.Fly != false) S.EntityOverlay.TaskManager.Enqueue(this.FlyIfCan);
        S.EntityOverlay.TaskManager.Enqueue(() => this.MoveToPosition(data, distance));
        S.EntityOverlay.TaskManager.Enqueue(() => this.WaitUntilArrival(data, distance), 10 * 60 * 1000);
        S.EntityOverlay.TaskManager.Enqueue(P.NavmeshManager.Stop);
        if (C.NavmeshAutoInteract && !data.NoInteract)
        {
            S.EntityOverlay.TaskManager.Enqueue(() =>
            {
                var obj = data.GetIGameObject();
                if (obj != null)
                {
                    S.EntityOverlay.TaskManager.Insert(() => this.InteractWithDataID(obj.DataId));
                }
            });
        }
    }

    public bool? FlyIfCan()
    {
        if (Utils.CanFly())
        {
            if (Svc.Condition[ConditionFlag.InFlight])
            {
                return true;
            }
            else
            {
                if (Svc.Condition[ConditionFlag.Jumping])
                {
                    EzThrottler.Throttle("Jump", 500, true);
                    Chat.Instance.ExecuteCommand($"/generalaction \"{Utils.GetGeneralActionName(2)}\"");
                }
                if (EzThrottler.Throttle("Jump"))
                {
                    Chat.Instance.ExecuteCommand($"/generalaction \"{Utils.GetGeneralActionName(2)}\"");
                }
            }
        }
        else
        {
            return true;
        }
        return false;
    }

    public bool? MountIfCan()
    {
        if (Svc.Condition[ConditionFlag.Mounted])
        {
            return true;
        }
        if (C.Mount == -1) return true;
        if (Svc.Condition[ConditionFlag.MountOrOrnamentTransition] || Svc.Condition[ConditionFlag.Casting])
        {
            EzThrottler.Throttle("CheckMount", 2000, true);
        }
        if (!EzThrottler.Check("CheckMount")) return false;
        if (ActionManager.Instance()->GetActionStatus(ActionType.GeneralAction, 9) == 0)
        {
            var mount = C.Mount;
            if (mount == 0 || !PlayerState.Instance()->IsMountUnlocked((uint)mount))
            {
                var mounts = Svc.Data.GetExcelSheet<Mount>().Where(x => x.Singular != "" && PlayerState.Instance()->IsMountUnlocked(x.RowId));
                if (mounts.Any())
                {
                    var newMount = (int)mounts.GetRandom().RowId;
                    PluginLog.Warning($"Mount {Utils.GetMountName(mount)} is not unlocked. Randomly selecting {Utils.GetMountName(newMount)}.");
                    mount = newMount;
                }
                else
                {
                    PluginLog.Warning("No unlocked mounts found");
                    return true;
                }
            }
            if (!Player.IsAnimationLocked && EzThrottler.Throttle("SummonMount"))
            {
                Chat.Instance.ExecuteCommand($"/mount \"{Utils.GetMountName(mount)}\"");
            }
        }
        else
        {
            return true;
        }
        return false;
    }

    public void MoveToPosition(MoveData data, float distance)
    {
        var pos = data.Position;
        if (Vector3.Distance(Player.Object.Position, pos) > distance)
        {
            this.LastPositionUpdate = Environment.TickCount64;
            this.LastPosition = Player.Position;
            P.NavmeshManager.PathfindAndMoveTo(pos, Svc.Condition[ConditionFlag.InFlight]);
        }
    }

    internal Vector3 LastPosition = Vector3.Zero;
    internal long LastPositionUpdate = 0;
    internal CircularBuffer<long> Unstucks = new(5);

    public bool? WaitUntilArrival(MoveData data, float distance)
    {
        if (!Player.Available) return null;
        if (!P.NavmeshManager.IsRunning())
        {
            this.LastPositionUpdate = Environment.TickCount64;
        }
        else
        {
            if (Vector3.Distance(this.LastPosition, Player.Position) > 0.5f)
            {
                this.LastPositionUpdate = Environment.TickCount64;
                this.LastPosition = Player.Position;
            }
        }
        if (data.Mount != false && C.Mount != -1 && Vector3.Distance(data.Position, Player.Object.Position) > 20f && !Svc.Condition[ConditionFlag.Mounted] && ActionManager.Instance()->GetActionStatus(ActionType.GeneralAction, 9) == 0)
        {
            this.EnqueueMoveAndInteract(data, distance);
            return false;
        }
        if (!data.NoInteract)
        {
            if (data.DataID == 0)
            {
                var obj = this.GetNearestMTQObject();
                if (obj != null)
                {
                    data.Position = obj.Position;
                    data.DataID = obj.DataId;
                    this.Log($"Correction to MTQ object: {obj.Name}/{obj.DataId:X8}");
                    this.MoveToPosition(data, distance);
                }
                else
                {
                    if (Vector3.Distance(data.Position, Player.Object.Position) < 30f)
                    {
                        foreach (var x in Svc.Objects.OrderBy(z => Vector3.Distance(data.Position, z.Position)))
                        {
                            if (Vector3.Distance(data.Position, x.Position) < 100f && x.ObjectKind.EqualsAny(ObjectKind.EventNpc | ObjectKind.EventObj) && x.IsTargetable)
                            {
                                data.Position = x.Position;
                                data.DataID = x.DataId;
                                this.Log($"Correction to non-MTQ object: {x.Name}/{x.DataId:X8}");
                                this.MoveToPosition(data, distance);
                                break;
                            }
                        }
                    }
                }
            }
        }
        var pos = data.Position;
        if (Environment.TickCount64 - this.LastPositionUpdate > 500 && EzThrottler.Throttle("RequeueMoveTo", 1000))
        {
            var cnt = this.Unstucks.Count(x => Environment.TickCount64 - x < 10000);
            if (cnt < 5)
            {
                this.Log($"Stuck, rebuilding path ({cnt + 1}/5)");
                this.MoveToPosition(data, distance);
                this.Unstucks.PushFront(Environment.TickCount64);
            }
            else
            {
                DuoLog.Error($"Stuck, move manually");
                P.NavmeshManager.Stop();
                return null;
            }
        }
        if (Vector3.Distance(Player.Object.Position, pos) > 12f && !Svc.Condition[ConditionFlag.Mounted] && !Svc.Condition[ConditionFlag.InCombat] && !Player.IsAnimationLocked && C.UseSprintPeloton)
        {
            if (ActionManager.Instance()->GetActionStatus(ActionType.Action, 3) == 0 && !Player.Object.StatusList.Any(z => z.StatusId == 50))
            {
                if (EzThrottler.Throttle("CastSprintPeloton", 2000))
                {
                    Chat.Instance.ExecuteCommand($"/action \"{Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Action>().GetRow(3).Name.ExtractText()}\"");
                }
            }
            else if (ActionManager.Instance()->GetActionStatus(ActionType.Action, 7557) == 0 && !Player.Object.StatusList.Any(z => z.StatusId.EqualsAny<uint>(1199, 50)))
            {
                if (EzThrottler.Throttle("CastSprintPeloton", 2000))
                {
                    Chat.Instance.ExecuteCommand($"/action \"{Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Action>().GetRow(7557).Name.ExtractText()}\"");
                }
            }
        }
        if (data.NoInteract)
        {
            if (Vector2.Distance(Player.Object.Position.ToVector2(), pos.ToVector2()) < distance)
            {
                this.Log("Stopped by 2D distance");
                return true;
            }
        }
        return Vector3.Distance(Player.Object.Position, pos) < distance;
    }

    public bool? InteractWithDataID(uint dataID)
    {
        if (!Player.Interactable) return false;
        if (dataID == 0) return true;
        if (Svc.Targets.Target != null)
        {
            var t = Svc.Targets.Target;
            if (t.IsTargetable && t.DataId == dataID && Vector3.Distance(Player.Object.Position, t.Position) < 10f && !IsOccupied() && !Player.IsAnimationLocked && Utils.ThrottleAutoInteract())
            {
                TargetSystem.Instance()->InteractWithObject(Svc.Targets.Target.Struct(), false);
                return true;
            }
        }
        else
        {
            foreach (var t in Svc.Objects)
            {
                if (t.IsTargetable && t.DataId == dataID && Vector3.Distance(Player.Object.Position, t.Position) < 10f && !IsOccupied() && EzThrottler.Throttle("SetTarget"))
                {
                    Svc.Targets.Target = t;
                    return false;
                }
            }
        }
        return false;
    }

    public void SpecialAdjust(MoveData data)
    {
        if (Player.Territory == 212) //adjust for walking sands
        {
            if (Player.Position.X < 24.5f && data.Position.X > 24.5f)
            {
                this.Log("Special adjustment: Entrance to the Solar at The Waking Sands");
                //new(2001715, 212, ObjectKind.EventObj, new(23.2f, 2.1f, -0.0f)), //Entrance to the Solar at The Waking Sands
                data.DataID = 2001715;
                data.Position = new(23.2f, 2.1f, -0.0f);
            }
            else if (Player.Position.X > 24.5f && data.Position.X < 24.5f)
            {
                this.Log("Special adjustment: Exit to the Waking Sands at The Waking Sands");
                //new(2001717, 212, ObjectKind.EventObj, new(25.5f, 2.1f, -0.0f)), //Exit to the Waking Sands at The Waking Sands
                data.DataID = 2001717;
                data.Position = new(25.5f, 2.1f, -0.0f);
            }
        }
        else if (Player.Territory == 351) //rising stones
        {
            if (Player.Position.Z < -28.0f && data.Position.Z > -28.0f)
            {
                this.Log("Special adjustment: Exit to the Rising Stones at The Rising Stones");
                //new(2002880, 351, ObjectKind.EventObj, new(-0.0f, -1.0f, -29.3f)), //Exit to the Rising Stones at The Rising Stones
                data.DataID = 2002880;
                data.Position = new(-0.0f, -1.0f, -29.3f);
            }
            else if (Player.Position.Z > -28.0f && data.Position.Z < -28.0f)
            {
                this.Log("Special adjustment: Entrance to the Solar at The Rising Stones");
                //new(2002878, 351, ObjectKind.EventObj, new(-0.0f, -1.0f, -26.8f)), //Entrance to the Solar at The Rising Stones
                data.DataID = 2002878;
                data.Position = new(-0.0f, -1.0f, -26.8f);
            }
        }
    }
}
