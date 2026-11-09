namespace PCL;

public partial class Setup
{
    public static void VersionRamType(int type)
    {
        if (Launcher.frmInstanceSetup is null)
            return;
        Launcher.frmInstanceSetup.RamType(type);
    }
}
