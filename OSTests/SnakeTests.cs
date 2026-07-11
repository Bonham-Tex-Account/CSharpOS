using CSharpOS;
using CSharpOSConsole;
using Xunit;

namespace OSTests;

/// <summary>
/// The snake game (Visualizer §3) and a regression for the scheduler bug it surfaced. Snake is the
/// first program with a multi-page DATA working set (grid + render buffer) that OUTS a full frame per
/// tick, which racks up hundreds of context switches — enough to hit the periodic cache flush that
/// used to clobber the scheduler's round-robin index (ECX). See EmitContextSwitch's cs_flush_skip.
/// </summary>
public class SnakeTests
{
    private static int Memory => Test.MachineWithHeap(32768);

    private static (List<string> frames, Hardware hw, BasicOS os) LoadSnake()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        List<string> frames = new List<string>();
        hw.ProgramOutput += (object? s, ProgramOutputArgs e) =>
        {
            if (e.StringValue != null) { frames.Add(e.StringValue); }
            hw.RaiseOutputComplete(e.Device);
        };
        os.LoadProcess(new Process(hw.Disk.Store(Programs.Snake()), 4096, 128));
        hw.SetActiveProcess(0);
        return (frames, hw, os);
    }

    [Fact]
    public void Snake_RendersGrid_RespondsToInput_AndEndsAtAWall()
    {
        (List<string> frames, Hardware hw, BasicOS os) = LoadSnake();

        // Let it render and start moving right, then steer down; it eventually hits a wall and exits.
        for (int i = 0; i < 5000; i++) { hw.Run(); }
        hw.RaiseKeyInterrupt(Hardware.KeyDown);
        for (int i = 0; i < 400000 && os.HasProcesses; i++) { hw.Run(); }

        Assert.NotEmpty(frames);
        // A rendered frame is a bordered grid with the snake, food and score.
        string first = frames[0];
        Assert.Contains("#", first);       // walls
        Assert.Contains("O", first);       // the snake body
        Assert.Contains("*", first);       // the food
        Assert.Contains("S:", first);      // the score line
        Assert.Contains("\n", first);      // multi-line frame (canvas mode renders it in place)
        // It steered down and ran to a wall: the game ended and the last frame announces it.
        Assert.False(os.HasProcesses);
        Assert.Contains("GAME OVER", frames[frames.Count - 1]);
    }

    [Fact]
    public void Snake_TurnsWhenSteered()
    {
        (List<string> frames, Hardware hw, BasicOS os) = LoadSnake();

        // Snake starts moving right (a horizontal "OOO"). Steer it down and it becomes vertical.
        for (int i = 0; i < 5000; i++) { hw.Run(); }
        int before = frames.Count;
        hw.RaiseKeyInterrupt(Hardware.KeyDown);
        for (int i = 0; i < 200000 && os.HasProcesses; i++) { hw.Run(); }

        // After steering, some frame shows two snake cells stacked vertically (a body cell directly
        // above another in the same column) — proof the direction changed and movement continued.
        bool sawVertical = false;
        for (int f = before; f < frames.Count; f++)
        {
            string[] rows = frames[f].Split('\n');
            for (int r = 0; r + 1 < rows.Length && !sawVertical; r++)
            {
                for (int c = 0; c < rows[r].Length && c < rows[r + 1].Length; c++)
                {
                    if (rows[r][c] == 'O' && rows[r + 1][c] == 'O')
                    {
                        sawVertical = true;
                    }
                }
            }
        }
        Assert.True(sawVertical, "snake should have turned vertical after a Down keypress");
    }

    // Regression: eating food must increment the score, relocate the food, and NOT hang. Before the
    // place_food fix, StR(RNG, EAX) clobbered EAX with the address before storing, so rng was pinned
    // to a constant — the food never moved (always the same cell) and eating it looped place_food
    // forever (it kept retrying the now-occupied fixed cell). Deterministic: seed 12345 places the
    // first food at (6,0); the snake starts at (4,4) moving right, so turning up column 6 eats it.
    [Fact]
    public void Snake_EatingFood_IncrementsScore_MovesTheFood_AndDoesNotHang()
    {
        (List<string> frames, Hardware hw, BasicOS os) = LoadSnake();

        HashSet<string> foodPositions = new HashSet<string>();
        bool ateFood = false;
        int lastFrameCount = 0;
        int steps = 0;
        // Whole game runs in ~300k steps; a place_food hang would never terminate, so the budget
        // being exhausted with the process still alive is the regression signal.
        while (os.HasProcesses && steps < 5_000_000)
        {
            hw.Run();
            steps++;
            if (frames.Count == lastFrameCount)
            {
                continue;
            }
            lastFrameCount = frames.Count;
            string f = frames[frames.Count - 1];
            if (f.Contains("S:001")) { ateFood = true; }
            string[] rows = f.Split('\n');
            for (int r = 0; r < rows.Length; r++)
            {
                int c = rows[r].IndexOf('*');
                if (c >= 0) { foodPositions.Add($"{c},{r}"); }
            }
            if (frames.Count == 3) { hw.RaiseKeyInterrupt(Hardware.KeyUp); } // turn up column 6 onto the food
        }

        Assert.False(os.HasProcesses, "snake did not terminate within the step budget (place_food hang?)");
        Assert.True(ateFood, "the snake should have eaten the food (score reached 1) without hanging");
        Assert.True(foodPositions.Count >= 2, "the food must move after being eaten (it was pinned before the fix)");
    }

    // Regression: a program that writes a multi-page DATA buffer and OUTS it in a loop generates many
    // context switches. After CacheFlushInterval of them the periodic cache_flush runs; it clobbers
    // ECX, which resume_mlfq uses as its round-robin index. Before the fix this scheduled a garbage
    // process index and crashed. The program must run for many thousands of steps without faulting.
    private static byte[] MultiPageOutputLoop()
    {
        Assembler asm = new Assembler();
        asm.Label("loop");
        asm.MovImm16(RegisterName.R13, 2048);          // a DATA buffer spanning several pages
        asm.MovImm(RegisterName.R9, 0);
        asm.Label("w");
        asm.MovImm16(RegisterName.R10, 200);
        asm.Cmp(RegisterName.R9, RegisterName.R10);
        asm.Jns("wdone");
        asm.MovImm(RegisterName.R11, 0x41);
        asm.Store(RegisterName.R13, RegisterName.R11);
        asm.MovImm(RegisterName.R10, 4);
        asm.Add(RegisterName.R13, RegisterName.R10);
        asm.Inc(RegisterName.R9);
        asm.Jmp("w");
        asm.Label("wdone");
        asm.MovImm(RegisterName.R11, 0);
        asm.Store(RegisterName.R13, RegisterName.R11);
        asm.MovImm16(RegisterName.EAX, 2048);
        asm.MovImm(RegisterName.ECX, 250);
        asm.Outs(RegisterName.EAX, RegisterName.ECX);
        asm.Jmp("loop");
        return asm.Build();
    }

    [Fact]
    public void Scheduler_SurvivesThePeriodicCacheFlush_UnderOutputHeavyLoad()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        int outputs = 0;
        hw.ProgramOutput += (object? s, ProgramOutputArgs e) => { outputs++; hw.RaiseOutputComplete(e.Device); };
        os.LoadProcess(new Process(hw.Disk.Store(MultiPageOutputLoop()), 4096, 128));
        hw.SetActiveProcess(0);

        // Run well past CacheFlushInterval context switches; the process loops forever, so no crash =
        // pass. (Before the ECX fix this threw an IndexOutOfRange from resume_mlfq within ~3 outputs.)
        for (int i = 0; i < 200000; i++) { hw.Run(); }

        // No crash from resume_mlfq = the fix holds (the old bug threw within ~3 outputs). A few
        // completed iterations confirm it kept scheduling past the periodic flush (thrashing on the
        // 4-frame pool makes each iteration slow, so the count is modest).
        Assert.True(outputs > 4, $"the output loop should have kept running across the periodic flush (got {outputs})");
    }
}
