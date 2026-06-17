namespace CSharpOS;

public class Computer
{
    private Hardware hardware;
    private OperatingSystem os;

    public Computer(OperatingSystem os, int memorySize, RegisterName[] registerNames, List<Process> starterProcesses)
    {
        this.os = os;
        hardware = new Hardware(memorySize, registerNames, os);

        foreach (Process process in starterProcesses)
        {
            os.LoadProcess(process);
        }
    }
}
