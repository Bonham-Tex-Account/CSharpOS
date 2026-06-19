using System.Collections.Concurrent;

namespace CSharpOS;

public class Hardware
{
    const int SchedulerInstructionCount = 10;

    // Fixed per-process kernel stack (scratch space, like a real kernel's
    // per-thread stack). The kernel section is sized separately to the OS's
    // kernel image (os.KernelImage), so it scales with the syscall library.
    public const int KernelStackSize = 64;

    // Kernel section header reserved by hardware, ahead of the kernel code:
    //   [0..63]  saved user register file (on a syscall trap)
    //   [64..]   trap-info: faulting opcode, operand byte-offset, return IP
    // The kernel image (code) is loaded at KernelHeaderSize and assembled with
    // that origin so its labels are section-relative. Assumes ≤16 registers.
    public const int KernelSaveAreaOffset = 0;
    public const int KernelTrapInfoOffset = 64;
    public const int KernelHeaderSize = 80;

    private byte[] memory;
    private byte[] registers;
    private Dictionary<RegisterName, int> registerIndex;
    private IOperatingSystem os;
    public int GetMemorySize() { return memory.Length; }

    private int instructionCount;
    private int instructionPointer;

    // Current privilege level. Boots in User. An I/O trap raises it to Kernel
    // (IRET lowers it); termination raises it to Privileged. Persisted per process
    // by the OS across context switches.
    private PrivilegeLevel level;

    // Set by a trap (EnterKernel / Halt / TrapInvalidInstruction) so Run does not
    // also report the faulting instruction as executed or tick the quantum.
    private bool trapTaken;

    private int currentProcessMemoryStart;
    private int currentProcessMemorySize;
    private int currentProcessStackStart;
    private int currentProcessStackSize;
    private int currentProcessKernelStackStart;
    private int currentProcessKernelStackSize;
    private int currentProcessInstructionStart;
    private int currentProcessInstructionSize;
    private int currentProcessKernelSectionStart;
    private int currentProcessKernelSectionSize;

    public event EventHandler<InstructionExecutedArgs>? InstructionExecuted;
    public event EventHandler<MemoryWrittenArgs>? MemoryWritten;
    public event EventHandler<InvalidInstructionArgs>? InvalidInstruction;
    public event EventHandler<ProgramOutputArgs>? ProgramOutput;

    // I/O devices. Input is buffered values delivered by input interrupts; output
    // is a single-transfer device that is busy until an output-complete interrupt.
    // Interrupts are raised out-of-band by the host (thread-safe) and drained by Run.
    private enum InterruptKind { InputReady, OutputComplete }

    private readonly struct Interrupt
    {
        public readonly InterruptKind Kind;
        public readonly int Value;
        public Interrupt(InterruptKind kind, int value) { Kind = kind; Value = value; }
    }

    private readonly Queue<int> inputBuffer = new Queue<int>();
    private bool outputBusy;
    private readonly ConcurrentQueue<Interrupt> pendingInterrupts = new ConcurrentQueue<Interrupt>();

    public void RaiseInputInterrupt(int value)
    {
        pendingInterrupts.Enqueue(new Interrupt(InterruptKind.InputReady, value));
    }

    public void RaiseOutputComplete()
    {
        pendingInterrupts.Enqueue(new Interrupt(InterruptKind.OutputComplete, 0));
    }

    public Hardware(int memorySize, RegisterName[] registerNames, IOperatingSystem os)
    {
        memory = new byte[memorySize];
        registers = new byte[registerNames.Length * 4];
        registerIndex = new Dictionary<RegisterName, int>();
        for (int i = 0; i < registerNames.Length; i++)
        {
            registerIndex[registerNames[i]] = i * 4;
        }
        this.os = os;
        instructionCount = 0;
        os.AttachHardware(this);
    }

    public int GetInstructionPointer() { return instructionPointer; }
    public void SetInstructionPointer(int address) { instructionPointer = address; }

    // Program-relative addressing follows the privilege level: kernel code runs
    // relative to the kernel section, user code relative to its program image.
    public int GetProgramBase()
    {
        return level == PrivilegeLevel.User ? currentProcessInstructionStart : currentProcessKernelSectionStart;
    }

    public PrivilegeLevel GetPrivilegeLevel() { return level; }
    public void SetPrivilegeLevel(PrivilegeLevel value) { level = value; }

    public void Output(int value)
    {
        ProgramOutput?.Invoke(this, new ProgramOutputArgs { Value = value });
    }

    // Kernel-mode input: deliver a buffered value, or block the process until an
    // input interrupt wakes it (then the IN instruction re-runs and succeeds).
    public void KernelInput(byte register)
    {
        if (inputBuffer.Count == 0)
        {
            BlockCurrent(WaitReason.Input);
            return;
        }
        WriteRegisterAt(register, inputBuffer.Dequeue());
    }

    // Kernel-mode output: deliver if the device is free (marking it busy until an
    // output-complete interrupt), otherwise block until it frees.
    public void KernelOutput(int value)
    {
        if (outputBusy)
        {
            BlockCurrent(WaitReason.Output);
            return;
        }
        Output(value);
        outputBusy = true;
    }

    // Rewinds IP to the I/O instruction so it re-runs on resume, then yields.
    private void BlockCurrent(WaitReason reason)
    {
        instructionPointer -= 4;
        trapTaken = true;
        os.BlockCurrentProcess(this, reason);
    }

    private void DrainInterrupts()
    {
        while (pendingInterrupts.TryDequeue(out Interrupt interrupt))
        {
            if (interrupt.Kind == InterruptKind.InputReady)
            {
                inputBuffer.Enqueue(interrupt.Value);
                os.Wake(WaitReason.Input);
            }
            else
            {
                outputBusy = false;
                os.Wake(WaitReason.Output);
            }
        }
    }

    // HLT is a request to terminate: an atomic OS-level (Privileged) operation.
    public void Halt()
    {
        level = PrivilegeLevel.Privileged;
        instructionCount = 0;
        trapTaken = true;
        os.HandleHalt(this);
    }

    // An I/O instruction executed in user mode traps into the kernel: the user
    // register file is saved into the kernel section, trap-info is recorded, and
    // control jumps to the kernel entry running on the process's kernel stack.
    public void EnterKernel(byte opcode, int operandByteOffset)
    {
        int kernelBase = currentProcessKernelSectionStart;
        WriteBytes(kernelBase + KernelSaveAreaOffset, (byte[])registers.Clone());
        WriteWord(kernelBase + KernelTrapInfoOffset, opcode);
        WriteWord(kernelBase + KernelTrapInfoOffset + 4, operandByteOffset);
        WriteWord(kernelBase + KernelTrapInfoOffset + 8, instructionPointer);
        level = PrivilegeLevel.Kernel;
        WriteRegister(RegisterName.ESP, currentProcessKernelStackStart + currentProcessKernelStackSize);
        instructionPointer = kernelBase + KernelHeaderSize;
        trapTaken = true;
    }

    // Returns from a kernel-mode syscall handler to the interrupted user code,
    // restoring the saved register file (incl. any IN result written to it).
    public void Iret()
    {
        int kernelBase = currentProcessKernelSectionStart;
        int returnIp = ReadWord(kernelBase + KernelTrapInfoOffset + 8);
        WriteRegisters(ReadRegisterState(kernelBase + KernelSaveAreaOffset));
        level = PrivilegeLevel.User;
        instructionPointer = returnIp;
    }

    private void WriteWord(int address, int value)
    {
        WriteBytes(address, new byte[]
        {
            (byte)(value & 0xFF),
            (byte)((value >> 8) & 0xFF),
            (byte)((value >> 16) & 0xFF),
            (byte)((value >> 24) & 0xFF)
        });
    }

    private int ReadWord(int address)
    {
        byte[] bytes = ReadBytes(address);
        return bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24);
    }

    public byte[] ReadBytes(int address)
    {
        return new byte[] { memory[address], memory[address + 1], memory[address + 2], memory[address + 3] };
    }

    public byte[] ReadRegisters()
    {
        return registers;
    }

    // Reads a full register-file-sized block from memory, used to restore a
    // saved register state. ReadBytes only returns a 4-byte instruction word.
    public byte[] ReadRegisterState(int address)
    {
        byte[] state = new byte[registers.Length];
        for (int i = 0; i < state.Length; i++)
        {
            state[i] = memory[address + i];
        }
        return state;
    }

    public void WriteRegisters(byte[] data)
    {
        for (int i = 0; i < data.Length; i++)
        {
            registers[i] = data[i];
        }
    }

    public void WriteBytes(int address, byte[] data)
    {
        for (int i = 0; i < data.Length; i++)
        {
            memory[address + i] = data[i];
        }
        MemoryWritten?.Invoke(this, new MemoryWrittenArgs { Address = address, Data = (byte[])data.Clone() });
    }

    public List<MemoryRange> GetCurrentProcessRanges()
    {
        List<MemoryRange> ranges = new List<MemoryRange>
        {
            new MemoryRange { Start = currentProcessMemoryStart, Size = currentProcessMemorySize },
            new MemoryRange { Start = currentProcessStackStart, Size = currentProcessStackSize },
            new MemoryRange { Start = currentProcessKernelStackStart, Size = currentProcessKernelStackSize },
            new MemoryRange { Start = currentProcessInstructionStart, Size = currentProcessInstructionSize },
            new MemoryRange { Start = currentProcessKernelSectionStart, Size = currentProcessKernelSectionSize }
        };

        ranges.Sort((MemoryRange a, MemoryRange b) => a.Start.CompareTo(b.Start));

        List<MemoryRange> merged = new List<MemoryRange>();
        MemoryRange current = ranges[0];

        for (int i = 1; i < ranges.Count; i++)
        {
            MemoryRange next = ranges[i];
            if (current.Start + current.Size >= next.Start)
            {
                current = new MemoryRange
                {
                    Start = current.Start,
                    Size = Math.Max(current.Start + current.Size, next.Start + next.Size) - current.Start
                };
            }
            else
            {
                merged.Add(current);
                current = next;
            }
        }

        merged.Add(current);
        return merged;
    }

    public void LoadProcess(Process process, byte[] program)
    {
        WriteBytes(process.ProgramAddress, program);
        process.ProgramSize = program.Length;
        process.ModeStateAddress = process.RegisterStateAddress + registers.Length;
        SetProcessLayout(process.ProgramAddress, program.Length, process.RequiredMemory, process.RequiredStackSize);
        if (os.KernelImage.Length > 0)
        {
            WriteBytes(currentProcessKernelSectionStart + KernelHeaderSize, os.KernelImage);
        }
        InitializeStackPointer(process);
    }

    // Restores the running process's memory layout so that program-relative
    // addressing and range freeing operate on the correct process.
    public void LoadProcessLayout(Process process)
    {
        SetProcessLayout(process.ProgramAddress, process.ProgramSize, process.RequiredMemory, process.RequiredStackSize);
    }

    // Layout: [program][kernel section][memory][user stack][kernel stack].
    // The kernel section is a hardware-reserved header (save area + trap-info)
    // followed by the OS kernel image. The register-state block and per-process
    // mode slot live at the front of the memory region (RegisterStateAddress ==
    // currentProcessMemoryStart).
    private void SetProcessLayout(int programAddress, int programSize, int requiredMemory, int requiredStackSize)
    {
        currentProcessInstructionStart = programAddress;
        currentProcessInstructionSize = programSize;
        currentProcessKernelSectionStart = programAddress + programSize;
        currentProcessKernelSectionSize = KernelHeaderSize + os.KernelImage.Length;
        currentProcessMemoryStart = currentProcessKernelSectionStart + currentProcessKernelSectionSize;
        currentProcessMemorySize = requiredMemory;
        currentProcessStackStart = currentProcessMemoryStart + requiredMemory;
        currentProcessStackSize = requiredStackSize;
        currentProcessKernelStackStart = currentProcessStackStart + requiredStackSize;
        currentProcessKernelStackSize = KernelStackSize;
    }

    // Seeds ESP to the top of the user stack (it grows down, so CALL's ESP-4
    // writes the first word inside the region) by writing it into the process's
    // saved register state, which the first context switch loads into ESP.
    private void InitializeStackPointer(Process process)
    {
        if (!registerIndex.ContainsKey(RegisterName.ESP))
        {
            return;
        }
        int userStackTop = currentProcessStackStart + currentProcessStackSize;
        int offset = registerIndex[RegisterName.ESP];
        WriteBytes(process.RegisterStateAddress + offset, new byte[]
        {
            (byte)(userStackTop & 0xFF),
            (byte)((userStackTop >> 8) & 0xFF),
            (byte)((userStackTop >> 16) & 0xFF),
            (byte)((userStackTop >> 24) & 0xFF)
        });
    }

    public int ReadRegisterAt(byte index)
    {
        int offset = index * 4;
        return registers[offset] | (registers[offset + 1] << 8) | (registers[offset + 2] << 16) | (registers[offset + 3] << 24);
    }

    public void WriteRegisterAt(byte index, int value)
    {
        int offset = index * 4;
        registers[offset] = (byte)(value & 0xFF);
        registers[offset + 1] = (byte)((value >> 8) & 0xFF);
        registers[offset + 2] = (byte)((value >> 16) & 0xFF);
        registers[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    public int ReadRegister(RegisterName name)
    {
        int offset = registerIndex[name];
        return registers[offset] | (registers[offset + 1] << 8) | (registers[offset + 2] << 16) | (registers[offset + 3] << 24);
    }

    public void WriteRegister(RegisterName name, int value)
    {
        int offset = registerIndex[name];
        registers[offset] = (byte)(value & 0xFF);
        registers[offset + 1] = (byte)((value >> 8) & 0xFF);
        registers[offset + 2] = (byte)((value >> 16) & 0xFF);
        registers[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    // An invalid opcode is a fault that terminates the process: an atomic OS-level
    // (Privileged) operation, like HLT. (No re-trap into the kernel — this is the
    // teardown path, not a syscall.)
    public void TrapInvalidInstruction(byte opcode, byte b1, byte b2, byte b3)
    {
        level = PrivilegeLevel.Privileged;
        InvalidInstruction?.Invoke(this, new InvalidInstructionArgs { Opcode = opcode, B1 = b1, B2 = b2, B3 = b3 });
        instructionCount = 0;
        trapTaken = true;
        os.HandleInvalidInstruction(this, opcode, b1, b2, b3);
    }

    public void Run()
    {
        DrainInterrupts();
        if (!os.HasRunningProcess)
        {
            os.Schedule(this);
        }
        if (!os.HasRunningProcess)
        {
            return; // idle: every process is blocked, waiting for an interrupt
        }

        int ip = instructionPointer;
        instructionPointer += 4;
        byte[] bytes = ReadBytes(ip);
        bool executed = Instruction.Execute(ip, this);
        if (!executed || trapTaken)
        {
            // The instruction trapped (invalid opcode, syscall, or termination): it
            // is not reported as executed and does not advance the quantum counter.
            trapTaken = false;
            return;
        }
        InstructionExecuted?.Invoke(this, new InstructionExecutedArgs { Address = ip, Opcode = bytes[0], B1 = bytes[1], B2 = bytes[2], B3 = bytes[3] });
        instructionCount++;
        // User and kernel code are preemptible; privileged OS primitives are atomic.
        if (level != PrivilegeLevel.Privileged && instructionCount >= SchedulerInstructionCount)
        {
            instructionCount = 0;
            os.ContextSwitch(this);
        }
    }
}
