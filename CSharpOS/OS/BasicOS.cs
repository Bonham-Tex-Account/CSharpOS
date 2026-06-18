namespace CSharpOS;

public class BasicOS : OperatingSystem
{
    public BasicOS(TextWriter log) : base(new List<Trap>(), log)
    {
    }
}
