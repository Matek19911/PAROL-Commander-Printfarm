using ParolFarm.Core.Kinematics;
using ParolFarm.Core.Math;
using ParolFarm.Core.Robot;
using ParolFarm.Core.Settings;
using ParolFarm.Core.Teach;
using Xunit;
using Xunit.Abstractions;
using static System.Math;

namespace ParolFarm.Core.Tests;

public class ProtocolTests
{
    [Fact]
    public void CommandFrame_HasOriginalLength_AndRoundTrips()
    {
        var f = new CommandFrame { Command = BoardCommand.Position, GripperPosition = -5 };
        int[] pos = { 1, -1, 8_000_000 - 1, -8_000_000, 0, 12345 };
        int[] spd = { -300, 300, 0, 22000, -22000, 7 };
        pos.CopyTo(f.Position, 0); spd.CopyTo(f.Speed, 0);
        f.InOut[IoIndex.Out1] = true;

        var bytes = Protocol.PackCommand(f);
        Assert.Equal(56, bytes.Length);
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 52 }, bytes[..4]);
        Assert.Equal(new byte[] { 1, 2 }, bytes[^2..]);
        Assert.Equal(0b0010_0000, bytes[4 + 38]); // OUT1 ist Bit 5 (Index 2 vom MSB)

        var p = new FrameParser();
        byte[]? payload = null;
        foreach (var b in bytes) payload = p.Push(b) ?? payload;
        Assert.NotNull(payload);
        var back = Protocol.UnpackCommand(payload);
        Assert.Equal(pos, back.Position);
        Assert.Equal(spd, back.Speed);
        Assert.Equal(BoardCommand.Position, back.Command);
        Assert.True(back.InOut[IoIndex.Out1]);
        Assert.Equal(-5, back.GripperPosition);
    }

    [Fact]
    public void Feedback_RoundTrips_WithGarbageBefore()
    {
        var f = new FeedbackFrame();
        f.Position[0] = -4242; f.Speed[5] = -1;
        f.Homed[0] = f.Homed[5] = true;
        f.InOut[IoIndex.EStop] = true;
        var bytes = Protocol.PackFeedback(f);
        Assert.Equal(60, bytes.Length);

        var p = new FrameParser();
        FeedbackFrame? got = null;
        foreach (var b in new byte[] { 0x12, 0xFF, 0x00, 0xFF, 0xFF }.Concat(bytes))
        {
            var pl = p.Push(b);
            if (pl != null) got = Protocol.UnpackFeedback(pl);
        }
        Assert.NotNull(got);
        Assert.Equal(-4242, got.Position[0]);
        Assert.Equal(-1, got.Speed[5]);
        Assert.True(got.Homed[0] && got.Homed[5] && !got.Homed[1]);
        Assert.True(got.EStopReleased);
    }
}

public class KinematicsTests
{
    readonly ITestOutputHelper _out;
    public KinematicsTests(ITestOutputHelper o) => _out = o;

    static double[] RandomQ(Random rnd)
    {
        var q = new double[6];
        for (int j = 0; j < 6; j++)
        {
            double min = Parol6Model.MinRad(j) + 0.15, max = Parol6Model.MaxRad(j) - 0.15;
            q[j] = min + rnd.NextDouble() * (max - min);
        }
        return q;
    }

    [Fact]
    public void Standby_IsPlausible()
    {
        var k = new Parol6Kinematics();
        var t = k.Forward(Parol6Model.StandbyRad);
        var p = t.ToMmDeg();
        _out.WriteLine($"Standby TCP: X={p.X:F1} Y={p.Y:F1} Z={p.Z:F1} mm  Rx={p.Rx:F1} Ry={p.Ry:F1} Rz={p.Rz:F1}°");
        Assert.InRange(t.P.Length, 0.1, 0.6);
    }

    [Fact]
    public void RotationVector_RoundTrips()
    {
        var rnd = new Random(1);
        for (int i = 0; i < 200; i++)
        {
            var rv = new Vec3(rnd.NextDouble() - 0.5, rnd.NextDouble() - 0.5, rnd.NextDouble() - 0.5).Normalized() * (rnd.NextDouble() * PI);
            var back = Rot3.FromRotationVector(rv).ToRotationVector();
            var diff = (Rot3.FromRotationVector(rv) * Rot3.FromRotationVector(back).Transposed()).ToRotationVector();
            Assert.True(diff.Length < 1e-6, $"{rv} -> {back}");
        }
        var pi = Rot3.FromRotationVector(new Vec3(0, PI, 0)).ToRotationVector();
        Assert.Equal(PI, pi.Length, 6);
    }

    [Fact]
    public void Jacobian_MatchesNumericDerivative()
    {
        var k = new Parol6Kinematics { Tool = new Pose(Rot3.RotX(0.3), new Vec3(0.01, 0.02, 0.08)) };
        var rnd = new Random(2);
        for (int n = 0; n < 20; n++)
        {
            var q = RandomQ(rnd);
            var j = k.Jacobian(q);
            var t0 = k.Forward(q);
            const double h = 1e-7;
            for (int c = 0; c < 6; c++)
            {
                var q2 = q.ToArray(); q2[c] += h;
                var t1 = k.Forward(q2);
                var dp = (t1.P - t0.P) / h;
                var dw = (t1.R * t0.R.Transposed()).ToRotationVector() / h;
                Assert.Equal(dp.X, j[0, c], 4); Assert.Equal(dp.Y, j[1, c], 4); Assert.Equal(dp.Z, j[2, c], 4);
                Assert.Equal(dw.X, j[3, c], 4); Assert.Equal(dw.Y, j[4, c], 4); Assert.Equal(dw.Z, j[5, c], 4);
            }
        }
    }

    [Fact]
    public void Inverse_FindsPose_FromNearbySeed()
    {
        var k = new Parol6Kinematics { Tool = new Pose(Rot3.Identity, new Vec3(0, 0, 0.06)) };
        var rnd = new Random(3);
        int ok = 0;
        for (int n = 0; n < 200; n++)
        {
            var q = RandomQ(rnd);
            var target = k.Forward(q);
            var seed = q.Select(v => v + (rnd.NextDouble() - 0.5) * 0.2).ToArray();
            var ik = k.Inverse(target, seed);
            if (!ik.Success) continue;
            ok++;
            var e = Parol6Kinematics.PoseError(target, k.Forward(ik.Q));
            Assert.True(Sqrt(e[0] * e[0] + e[1] * e[1] + e[2] * e[2]) < 2e-5);
        }
        _out.WriteLine($"IK erfolgreich: {ok}/200");
        Assert.True(ok >= 190, $"nur {ok}/200");
    }

    [Fact]
    public void LinearPlan_StaysOnStraightLine()
    {
        var k = new Parol6Kinematics();
        var planner = new TrajectoryPlanner(k);
        var q0 = Parol6Model.StandbyRad;
        q0[4] = 0.6; // nicht in der Handgelenk-Singularität (J5 = 0) starten
        var start = k.Forward(q0);
        var target = new Pose(start.R, start.P + new Vec3(0.03, -0.04, -0.05));
        var plan = planner.PlanLinear(q0, target, 0.05, 0.5);
        Assert.True(plan.Success, plan.Error);
        var dir = (target.P - start.P).Normalized();
        foreach (var q in plan.Trajectory!.Points)
        {
            var p = k.Forward(q).P - start.P;
            var off = p - dir * p.Dot(dir);
            Assert.True(off.Length < 5e-5, $"Abweichung {off.Length * 1000:F3} mm");
        }
        var end = k.Forward(plan.Trajectory.Points[^1]);
        Assert.True((end.P - target.P).Length < 2e-5);
        _out.WriteLine($"Dauer: {plan.Trajectory.Duration:F2} s");
        Assert.InRange(plan.Trajectory.Duration, 7.07 / 5, 7.07 / 5 + 1.0);
    }

    [Fact]
    public void LinearPlan_OutOfWristSingularity_IsRejected()
    {
        var k = new Parol6Kinematics();
        var planner = new TrajectoryPlanner(k);
        var q0 = Parol6Model.StandbyRad; // J5 = 0 -> J4 und J6 fluchten
        var start = k.Forward(q0);
        var plan = planner.PlanLinear(q0, new Pose(start.R, start.P + new Vec3(0.03, -0.04, -0.05)), 0.05, 0.5);
        Assert.False(plan.Success);
        Assert.Contains("Singularität", plan.Error);
    }

    [Fact]
    public void TrapezoidProfile_IsMonotonicAndEndsAtOne()
    {
        var s = TrajectoryPlanner.TrapezoidProfile(0.5, 2, 0.01);
        Assert.Equal(1, s[^1]);
        for (int i = 1; i < s.Count; i++) Assert.True(s[i] >= s[i - 1]);
        var tri = TrajectoryPlanner.TrapezoidProfile(10, 1, 0.01);
        Assert.Equal(1, tri[^1]);
    }
}

public class SpaceMouseTests
{
    [Fact]
    public void ParsesSplitAndCombinedReports()
    {
        var s = new SpaceMouseState();
        s.ApplyReport(new byte[] { 1, 0x5E, 0x01, 0xA2, 0xFE, 0, 0 });
        Assert.Equal(350, s.Tx); Assert.Equal(-350, s.Ty);
        s.ApplyReport(new byte[] { 2, 0, 0, 0, 0, 0x5E, 0x01 });
        Assert.Equal(350, s.Rz);
        s.ApplyReport(new byte[] { 1, 0, 0, 0, 0, 0, 0, 1, 0, 2, 0, 3, 0 });
        Assert.Equal(1, s.Rx); Assert.Equal(3, s.Rz);
        s.ApplyReport(new byte[] { 3, 0x02, 0 });
        Assert.Equal(2u, s.Buttons);
    }

    [Fact]
    public void Mapper_AppliesDeadbandAndDominantAxis()
    {
        var cfg = new SpaceMouseSettings { DominantAxisOnly = true };
        var s = new SpaceMouseState { Tx = 20, Ty = 350, Rz = 200 };
        var (lin, ang) = SpaceMouseMapper.Map(s, cfg);
        Assert.Equal(0, lin.X);
        Assert.Equal(-1, lin.Y, 6);
        Assert.Equal(Vec3.Zero, ang);
    }
}

public class ControllerSimulationTests
{
    static async Task<(RobotController, SimulatedRobotLink)> ReadyRobot()
    {
        var rc = new RobotController { SpeedOverride = 1.0 };
        var sim = new SimulatedRobotLink();
        rc.Connect(sim);
        await WaitFor(() => rc.Status.FeedbackOk, 2000);
        rc.Home();
        await WaitFor(() => rc.Status.AllHomed, 4000);
        rc.Enable();
        await Task.Delay(100);
        Assert.Null(rc.MotionBlockReason());
        return (rc, sim);
    }

    static async Task WaitFor(Func<bool> cond, int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException();
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task MoveJ_And_MoveL_ReachTargets()
    {
        var (rc, _) = await ReadyRobot();
        using var _rc = rc;
        var target = Parol6Model.StandbyRad;
        target[0] += 0.3; target[4] += 0.4;
        Assert.True(await rc.MoveJAsync(target, 1.0));
        await Task.Delay(50);
        var q = rc.CurrentJoints();
        for (int j = 0; j < 6; j++) Assert.Equal(target[j], q[j], 3);

        var start = rc.Kinematics.Forward(q);
        var goal = new Pose(start.R, start.P + new Vec3(0, 0, -0.03));
        Assert.True(await rc.MoveLAsync(goal, 0.06));
        await Task.Delay(50);
        var reached = rc.Kinematics.Forward(rc.CurrentJoints());
        Assert.True((reached.P - goal.P).Length < 0.0005);
    }

    [Fact]
    public async Task CartesianJog_MovesAlongBaseX_AndStopsWithoutUpdates()
    {
        var (rc, _) = await ReadyRobot();
        using var _rc = rc;
        var p0 = rc.Status.Tcp.P;
        var until = DateTime.UtcNow.AddMilliseconds(800);
        while (DateTime.UtcNow < until)
        {
            rc.JogCartesian(new Vec3(1, 0, 0), Vec3.Zero, JogFrame.Base);
            await Task.Delay(20);
        }
        // keine Vorgaben mehr -> Watchdog stoppt
        await WaitFor(() => rc.Status.Motion == MotionState.Idle, 1500);
        var p1 = rc.Status.Tcp.P;
        var d = p1 - p0;
        Assert.True(d.X > 0.01, $"dx={d.X}");
        Assert.True(Abs(d.Y) < 0.001 && Abs(d.Z) < 0.001, $"Drift {d}");
        var r = (rc.Status.Tcp.R * rc.Kinematics.Forward(Parol6Model.StandbyRad).R.Transposed()).ToRotationVector();
        Assert.True(r.Length < 0.002, "Orientierung muss beim linearen Jog gleich bleiben");
    }

    [Fact]
    public async Task EStop_AbortsMotion_AndBlocksUntilReenabled()
    {
        var (rc, sim) = await ReadyRobot();
        using var _rc = rc;
        var target = Parol6Model.StandbyRad;
        target[0] += 1.0;
        var move = rc.MoveJAsync(target, 0.3);
        await Task.Delay(300);
        sim.EStopPressed = true;
        Assert.False(await move);
        Assert.True(rc.Status.EStop);
        Assert.NotNull(rc.MotionBlockReason());
        sim.EStopPressed = false;
        await WaitFor(() => !rc.Status.EStop, 1000);
        Assert.Equal("Roboter nicht freigegeben", rc.MotionBlockReason());
    }

    [Fact]
    public async Task StopMotion_DeceleratesAndFails()
    {
        var (rc, _) = await ReadyRobot();
        using var _rc = rc;
        var target = Parol6Model.StandbyRad;
        target[0] += 1.5;
        var move = rc.MoveJAsync(target, 0.5);
        await Task.Delay(500);
        rc.StopMotion();
        Assert.False(await move);
        Assert.True(rc.CurrentJoints()[0] < target[0] - 0.1);
    }

    [Fact]
    public async Task ProgramRunner_RunsTaughtProgram()
    {
        var (rc, _) = await ReadyRobot();
        using var _rc = rc;
        var work = Parol6Model.StandbyRad;
        work[4] = 0.6;
        Assert.True(await rc.MoveJAsync(work, 1.0));
        await Task.Delay(50);
        var home = rc.CurrentJoints();
        var above = home.ToArray(); above[0] += 0.2;
        var project = new RobotProject();
        project.Waypoints.Add(new Waypoint { Name = "Home", JointsDeg = home.Select(v => v * 180 / PI).ToArray() });
        project.Waypoints.Add(new Waypoint { Name = "Ueber Teil", JointsDeg = above.Select(v => v * 180 / PI).ToArray() });
        var prog = new RobotProgram
        {
            Name = "Test",
            Steps =
            {
                new MoveJStep { Waypoint = "Ueber Teil", SpeedPercent = 100 },
                new GripperStep { Close = true, WaitMs = 50 },
                new MoveLStep { Waypoint = "Home", SpeedMmS = 60 },
                new GripperStep { Close = false, WaitMs = 50 },
            }
        };
        var runner = new ProgramRunner(rc);
        Assert.True(await runner.RunAsync(prog, project, CancellationToken.None));
        Assert.False(rc.GripperClosed);

        var path = Path.Combine(Path.GetTempPath(), $"parol-{Guid.NewGuid()}.json");
        project.Programs.Add(prog);
        project.Save(path);
        var loaded = RobotProject.Load(path);
        Assert.IsType<MoveLStep>(loaded.Programs[0].Steps[2]);
        File.Delete(path);
    }
}
