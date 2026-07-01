using CSharpOS;

namespace CSharpOSConsole;

/// <summary>
/// Builds the demo programs as assembled byte images.
/// </summary>
public static class Programs
{
    // Counts 1..10, printing each value, then halts.
    public static byte[] CounterToTen()
    {
        Assembler asm = new Assembler();
        asm.MovImm(RegisterName.EAX, 0);    // counter
        asm.MovImm(RegisterName.EBX, 10);   // limit
        asm.Label("loop");
        asm.Inc(RegisterName.EAX);          // counter++
        asm.Out(RegisterName.EAX);          // print counter
        asm.Cmp(RegisterName.EAX, RegisterName.EBX);
        asm.Jnz("loop");                    // repeat until counter == 10
        asm.Hlt();
        return asm.Build();
    }

    // Builds the list [10, 20, 30, 40] in memory, sums it, prints the average (25).
    public static byte[] AverageOfList()
    {
        Assembler asm = new Assembler();

        asm.MovImm(RegisterName.ESI, 4);          // ESI = element stride (4 bytes)

        // --- write the list into the data area ---
        asm.MovImmLabel(RegisterName.EBX, "data"); // EBX = data pointer
        asm.MovImm(RegisterName.EAX, 10);
        asm.Store(RegisterName.EBX, RegisterName.EAX);
        asm.Add(RegisterName.EBX, RegisterName.ESI);
        asm.MovImm(RegisterName.EAX, 20);
        asm.Store(RegisterName.EBX, RegisterName.EAX);
        asm.Add(RegisterName.EBX, RegisterName.ESI);
        asm.MovImm(RegisterName.EAX, 30);
        asm.Store(RegisterName.EBX, RegisterName.EAX);
        asm.Add(RegisterName.EBX, RegisterName.ESI);
        asm.MovImm(RegisterName.EAX, 40);
        asm.Store(RegisterName.EBX, RegisterName.EAX);

        // --- sum the list ---
        asm.MovImmLabel(RegisterName.EBX, "data"); // reset pointer
        asm.MovImm(RegisterName.ECX, 0);           // sum
        asm.MovImm(RegisterName.EDX, 4);           // remaining count
        asm.Label("sum");
        asm.Load(RegisterName.EAX, RegisterName.EBX);  // EAX = *ptr
        asm.Add(RegisterName.ECX, RegisterName.EAX);   // sum += EAX
        asm.Add(RegisterName.EBX, RegisterName.ESI);   // ptr += 4
        asm.Dec(RegisterName.EDX);                     // count--
        asm.Jnz("sum");

        // --- average = sum / 4 ---
        asm.MovImm(RegisterName.EDI, 4);
        asm.Mov(RegisterName.EAX, RegisterName.ECX);
        asm.Div(RegisterName.EAX, RegisterName.EDI);
        asm.Out(RegisterName.EAX);                 // print average
        asm.Hlt();

        asm.DataInt("data");   // slot 0
        asm.DataInt("d1");     // slot 1
        asm.DataInt("d2");     // slot 2
        asm.DataInt("d3");     // slot 3
        return asm.Build();
    }

    // Busy-works for `iterations` loop turns (a countdown), then prints `printValue`
    // and halts. Non-interactive and self-terminating, so it is ideal for memory-churn
    // demos: several can coexist (filling the buddy heap) and then drain as they finish.
    // Both arguments must fit in a byte (0..255).
    public static byte[] BusyThenHalt(int iterations, int printValue)
    {
        Assembler asm = new Assembler();
        asm.MovImm(RegisterName.EAX, iterations);
        asm.Label("spin");
        asm.Dec(RegisterName.EAX);
        asm.Jnz("spin");
        asm.MovImm(RegisterName.EAX, printValue);
        asm.Out(RegisterName.EAX);
        asm.Hlt();
        return asm.Build();
    }

    // A tiny shell: read a command id (a disk slot) via IN, fork, have the child EXEC
    // that program while the parent focuses the child and waits for it, then loop. This
    // exercises the whole spawning family (FORK / EXEC / SETFOCUS / WAIT) end to end.
    public static byte[] Shell()
    {
        Assembler asm = new Assembler();
        asm.Label("loop");
        asm.In(RegisterName.EAX);                       // read a command id (disk slot)
        asm.Mov(RegisterName.ECX, RegisterName.EAX);    // save it (FORK clears the child's EAX)
        asm.Fork();
        asm.MovImm(RegisterName.EBX, 0);
        asm.Cmp(RegisterName.EAX, RegisterName.EBX);
        asm.Jnz("parent");
        // Child (EAX == 0): become the requested program.
        asm.Exec(RegisterName.ECX);
        // Parent (EAX == child PID): focus the child, wait for it, then prompt again.
        asm.Label("parent");
        asm.SetFocus(RegisterName.EAX);
        asm.Wait(RegisterName.EAX);
        asm.Jmp("loop");
        return asm.Build();
    }

    // Parent forks three children with different lifetimes, then waits for all three.
    // Children output 1/2/3; parent outputs 0 last. Produces a 4-node tree.
    // WAIT clobbers EAX with the exit status, so each child PID is saved to a
    // dedicated register (ECX/EDX/ESI) before any WAIT runs.
    public static byte[] SpawnChildren()
    {
        Assembler asm = new Assembler();
        asm.MovImm(RegisterName.EBX, 0);             // comparison constant (child EAX after FORK)

        // Fork child 1.
        asm.Fork();
        asm.Cmp(RegisterName.EAX, RegisterName.EBX);
        asm.Jnz("parent1");
        // Child 1: short busy work, then halt.
        asm.MovImm(RegisterName.EAX, 100);
        asm.Label("spin1");
        asm.Dec(RegisterName.EAX);
        asm.Jnz("spin1");
        asm.MovImm(RegisterName.EAX, 1);
        asm.Out(RegisterName.EAX);
        asm.Hlt();
        asm.Label("parent1");
        asm.Mov(RegisterName.ECX, RegisterName.EAX); // save child 1 pid

        // Fork child 2.
        asm.Fork();
        asm.Cmp(RegisterName.EAX, RegisterName.EBX);
        asm.Jnz("parent2");
        // Child 2: medium busy work, then halt.
        asm.MovImm(RegisterName.EAX, 200);
        asm.Label("spin2");
        asm.Dec(RegisterName.EAX);
        asm.Jnz("spin2");
        asm.MovImm(RegisterName.EAX, 2);
        asm.Out(RegisterName.EAX);
        asm.Hlt();
        asm.Label("parent2");
        asm.Mov(RegisterName.EDX, RegisterName.EAX); // save child 2 pid

        // Fork child 3.
        asm.Fork();
        asm.Cmp(RegisterName.EAX, RegisterName.EBX);
        asm.Jnz("parent3");
        // Child 3: long busy work, then halt.
        asm.MovImm(RegisterName.EAX, 150);
        asm.Label("spin3");
        asm.Dec(RegisterName.EAX);
        asm.Jnz("spin3");
        asm.MovImm(RegisterName.EAX, 3);
        asm.Out(RegisterName.EAX);
        asm.Hlt();
        asm.Label("parent3");
        asm.Mov(RegisterName.ESI, RegisterName.EAX); // save child 3 pid

        // All three pids saved; now wait for each in turn.
        asm.Wait(RegisterName.ECX);                  // wait for child 1
        asm.Wait(RegisterName.EDX);                  // wait for child 2
        asm.Wait(RegisterName.ESI);                  // wait for child 3
        asm.MovImm(RegisterName.EAX, 0);
        asm.Out(RegisterName.EAX);
        asm.Hlt();
        return asm.Build();
    }

    // Interactive guessing game. Secret = 42. Reads guesses via IN, prints a hint
    // code (1 = too low, 2 = too high) until the guess is correct, then prints it.
    public static byte[] GuessingGame()
    {
        Assembler asm = new Assembler();
        asm.MovImm(RegisterName.EBX, 42);   // secret
        asm.MovImm(RegisterName.ECX, 1);    // "too low" code
        asm.MovImm(RegisterName.EDX, 2);    // "too high" code
        asm.Label("guess");
        asm.In(RegisterName.EAX);           // read a guess
        asm.Cmp(RegisterName.EAX, RegisterName.EBX);
        asm.Jz("correct");
        asm.Js("toolow");                   // guess < secret
        asm.Out(RegisterName.EDX);          // too high
        asm.Jmp("guess");
        asm.Label("toolow");
        asm.Out(RegisterName.ECX);          // too low
        asm.Jmp("guess");
        asm.Label("correct");
        asm.Out(RegisterName.EAX);          // the answer
        asm.Hlt();
        return asm.Build();
    }
}
