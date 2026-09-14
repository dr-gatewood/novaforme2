using System.Windows.Media;

namespace Nova4Me2.App.Services;

/// <summary>Small vector icons (24x24 viewbox) so the UI does not depend on icon fonts.</summary>
public static class Icons
{
    private static readonly Dictionary<string, string> Data = new()
    {
        ["drives"] = "M3,5 H21 A1,1 0 0 1 22,6 V10 A1,1 0 0 1 21,11 H3 A1,1 0 0 1 2,10 V6 A1,1 0 0 1 3,5 Z M3,13 H21 A1,1 0 0 1 22,14 V18 A1,1 0 0 1 21,19 H3 A1,1 0 0 1 2,18 V14 A1,1 0 0 1 3,13 Z M18,7.5 A0.9,0.9 0 1 0 18.01,7.5 Z M18,15.5 A0.9,0.9 0 1 0 18.01,15.5 Z",
        ["browse"] = "M3,6 A2,2 0 0 1 5,4 H9 L11,6 H19 A2,2 0 0 1 21,8 V18 A2,2 0 0 1 19,20 H5 A2,2 0 0 1 3,18 Z",
        ["recover"] = "M12,3 V13 M8,9 L12,13 L16,9 M4,15 V18 A2,2 0 0 0 6,20 H18 A2,2 0 0 0 20,18 V15",
        ["clone"] = "M8,4 H18 A2,2 0 0 1 20,6 V16 M4,8 H14 A2,2 0 0 1 16,10 V20 H6 A2,2 0 0 1 4,18 Z",
        ["health"] = "M3,12 H7 L9,6 L12,18 L15,10 L17,12 H21",
        ["repair"] = "M14.5,4 A5,5 0 0 0 9.8,10.6 L4,16.4 L7.6,20 L13.4,14.2 A5,5 0 0 0 20,9.5 L17,12.5 L14,11.5 L13,8.5 Z",
        ["firmware"] = "M6,6 H18 V18 H6 Z M9,9 H15 V15 H9 Z M9,3 V6 M12,3 V6 M15,3 V6 M9,18 V21 M12,18 V21 M15,18 V21 M3,9 H6 M3,12 H6 M3,15 H6 M18,9 H21 M18,12 H21 M18,15 H21",
        ["info"] = "M12,3 A9,9 0 1 0 12.01,3 Z M12,10 V17 M12,7 V7.5",
        ["settings"] = "M12,8 A4,4 0 1 0 12.01,8 Z M12,2 V5 M12,19 V22 M2,12 H5 M19,12 H22 M4.9,4.9 L7,7 M17,17 L19.1,19.1 M4.9,19.1 L7,17 M17,7 L19.1,4.9",
        ["help"] = "M12,3 A9,9 0 1 0 12.01,3 Z M9.5,9.5 A2.5,2.5 0 1 1 12,12.5 V14 M12,17 V17.5",
        ["mount"] = "M4,7 H20 V17 H4 Z M8,20 H16 M12,17 V20 M7,10 H13",
        ["folder"] = "M3,6 A2,2 0 0 1 5,4 H9 L11,6 H19 A2,2 0 0 1 21,8 V18 A2,2 0 0 1 19,20 H5 A2,2 0 0 1 3,18 Z",
        ["file"] = "M6,3 H14 L19,8 V21 H6 Z M14,3 V8 H19",
        ["refresh"] = "M20,12 A8,8 0 1 1 17.5,6.2 M20,4 V8.5 H15.5",
        ["copy"] = "M8,4 H18 A2,2 0 0 1 20,6 V16 M4,8 H14 A2,2 0 0 1 16,10 V20 H6 A2,2 0 0 1 4,18 Z",
        ["play"] = "M7,4 L19,12 L7,20 Z",
        ["stop"] = "M6,6 H18 V18 H6 Z",
        ["warning"] = "M12,3 L22,20 H2 Z M12,9 V14 M12,16.5 V17",
        ["check"] = "M4,12 L9,17 L20,6",
        ["link"] = "M10,14 A4,4 0 0 0 15.7,14 L18.5,11.2 A4,4 0 0 0 12.8,5.5 L11.5,6.8 M14,10 A4,4 0 0 0 8.3,10 L5.5,12.8 A4,4 0 0 0 11.2,18.5 L12.5,17.2",
        ["search"] = "M10,3 A7,7 0 1 0 10.01,3 Z M15,15 L21,21 M7,10 H13 M10,7 V13",
        ["usb"] = "M12,2 L14.5,5 H9.5 Z M12,5 V20 M12,20 A1.5,1.5 0 1 0 12.01,20 Z M12,13 L16,11 V8 H18 M12,16 L8,14 V11 A1.5,1.5 0 1 0 8,11",
    };

    public static Geometry Get(string name)
    {
        var g = Geometry.Parse(Data.TryGetValue(name, out var d) ? d : Data["info"]);
        g.Freeze();
        return g;
    }
}
