using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace FbtCal
{
    // The pictures built into the exe (tray\res, made by art\make_icons.py): the icons and the
    // panel's header and dashboard-tab images.
    static class Res
    {
        static Stream Open(string name) =>
            typeof(Res).Assembly.GetManifestResourceStream(name) ?? throw new FileNotFoundException("resource " + name);

        // A tray icon at the size Windows draws it (16 px at 100 %, more on high-DPI screens).
        public static Icon TrayIcon(string name)
        {
            using var s = Open(name);
            return new Icon(s, SystemInformation.SmallIconSize);
        }

        public static Bitmap Image(string name)
        {
            using var s = Open(name);
            return new Bitmap(s);
        }
    }
}
