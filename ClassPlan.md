# CSharpOS Class Plan

---

## Hardware Class

**Fields:**
- `byte[] memory` — sized on construction
- `byte[] registers` — sized on construction
- `enum RegisterName` — passed in on construction to map names to register indices
- `OperatingSystem os` — reference to the OS
- `int currentProcessMemoryStart` — start address of the current process's memory region
- `int currentProcessMemorySize` — size of the current process's memory region
- `int currentProcessStackStart` — start address of the current process's stack region
- `int currentProcessStackSize` — size of the current process's stack region
- `int currentProcessInstructionStart` — start address of the current process's instruction region in memory
- `int currentProcessInstructionSize` — size of the current process's instruction region in memory

**Constructor:**
- Takes memory size and register names/count as parameters

**Methods:**
- `Run()` — executes a single instruction (implementation TBD)

---

## OperatingSystem Class

**Fields:**
- `int instructionPointer` — the true instruction pointer
- `List<Process> processes` — all loaded processes
- `Process currentProcess` — the currently running process
- `int instructionCount` — tracks how many instructions the current process has run since last context switch
- `List<Trap> traps` — trap definitions loaded into hardware (each Trap contains an instruction and/or invalid condition)
- `List<MemoryRange> availableMemoryRanges` — list of free memory ranges available to load programs into

**Methods:**
- `HandleInvalidInstruction(/* some hardware data */)` — called by hardware when an invalid instruction is trapped

---

## Process Class

**Fields:**
- `int registerStateAddress` — pointer to where register state is saved in hardware memory
- `int instructionPointer` — current position in the program
- `string programFilePath` — file path to the program, loaded by the OS at process start
- `int programAddress` — pointer to where the program is stored in hardware memory
- `int requiredMemory` — how much memory this process needs
- `int requiredStackSize` — how much stack space this process needs

---

## Instruction Class (static)

**Fields:**
- `static Dictionary<byte, Action<Hardware, byte, byte, byte>> opcodeTable` — maps opcode byte to a handler that takes the Hardware instance and 3 parameter bytes

**Methods:**
- `static Execute(int address, Hardware hw)` — reads 4 bytes from memory at address, extracts opcode and 3 parameter bytes, looks up and calls the handler

---

## MemoryRange (structure)

**Fields:**
- `int start` — start address of the memory region
- `int size` — size of the memory region

---

## Trap (structure)

**Fields:**
- `byte opcode` — the opcode to watch for
- `Func<Hardware, byte, byte, byte, bool> condition` — takes the Hardware instance and 3 parameter bytes, returns true if the instruction is invalid
