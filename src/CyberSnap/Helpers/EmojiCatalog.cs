namespace CyberSnap.Helpers;

/// <summary>
/// Shared, searchable emoji palette used by the capture overlay's emoji tool and the
/// editor's emoji picker. Names include semantic search tags after a '|' separator.
/// </summary>
public static class EmojiCatalog
{
    public static readonly (string emoji, string name)[] Items =
    {
        // Smileys
        ("\U0001F600", "grinning|happy face"), ("\U0001F603", "smiley|happy"), ("\U0001F604", "smile|happy"),
        ("\U0001F601", "grin|happy teeth"), ("\U0001F605", "sweat smile|nervous"), ("\U0001F602", "joy|laugh crying"),
        ("\U0001F923", "rofl|funny"), ("\U0001F609", "wink|flirt"), ("\U0001F60A", "blush|happy shy"),
        ("\U0001F607", "innocent|angel halo"), ("\U0001F60D", "heart eyes|love"), ("\U0001F929", "star struck|amazed"),
        ("\U0001F618", "kiss|love blow"), ("\U0001F617", "kissing"), ("\U0001F61B", "tongue|silly playful"),
        ("\U0001F92A", "zany|crazy wild"), ("\U0001F60E", "sunglasses|cool"), ("\U0001F913", "nerd|smart glasses"),
        ("\U0001F914", "thinking|hmm wonder"), ("\U0001F928", "raised eyebrow|skeptical doubt"), ("\U0001F610", "neutral|meh"),
        ("\U0001F611", "expressionless|blank"), ("\U0001F636", "no mouth|silent quiet"), ("\U0001F644", "rolling eyes|annoyed"),
        ("\U0001F60F", "smirk|sly"), ("\U0001F62C", "grimacing|awkward cringe"), ("\U0001F925", "lying|pinocchio"),
        ("\U0001F60C", "relieved|calm peaceful"), ("\U0001F614", "pensive|sad thoughtful"), ("\U0001F62A", "sleepy|tired"),
        ("\U0001F924", "drooling"), ("\U0001F634", "sleeping|zzz"), ("\U0001F637", "mask|sick covid"),
        ("\U0001F912", "thermometer|sick fever"), ("\U0001F915", "bandage|hurt injured"), ("\U0001F922", "nauseated|sick gross"),
        ("\U0001F92E", "vomiting|sick gross"), ("\U0001F927", "sneezing|sick cold"), ("\U0001F975", "hot|warm sweating"),
        ("\U0001F976", "cold|freezing"), ("\U0001F974", "woozy|drunk"), ("\U0001F635", "dizzy|confused"),
        ("\U0001F92F", "exploding head|mind blown wow"), ("\U0001F920", "cowboy"), ("\U0001F973", "partying|party celebrate"),
        ("\U0001F978", "disguise|spy hidden"), ("\U0001F62D", "crying|sob sad tears"),
        ("\U0001F622", "cry|tear sad"), ("\U0001F625", "sad|disappointed"), ("\U0001F624", "angry huff|frustrated"),
        ("\U0001F621", "angry|mad"), ("\U0001F620", "rage|furious"), ("\U0001F92C", "swearing|cursing"),
        ("\U0001F608", "devil|evil"), ("\U0001F47F", "imp|evil"), ("\U0001F480", "skull|dead death"),
        ("\U0001F4A9", "poop|shit"), ("\U0001F921", "clown|funny"), ("\U0001F47B", "ghost|spooky"),
        ("\U0001F47D", "alien|space ufo"), ("\U0001F916", "robot|tech"), ("\U0001F63A", "cat smile"),
        // Gestures & People
        ("\U0001F44D", "thumbs up|yes good ok like approve"), ("\U0001F44E", "thumbs down|no bad dislike reject"), ("\U0001F44F", "clap|applause bravo"),
        ("\U0001F64C", "raised hands|hooray celebrate"), ("\U0001F91D", "handshake|deal agree"), ("\U0001F64F", "pray|please thanks hope"),
        ("\U0000270D", "writing hand|note"), ("\U0001F4AA", "muscle|strong power flex"), ("\U0001F449", "point right|this here"),
        ("\U0001F448", "point left"), ("\U0001F446", "point up|look above"), ("\U0001F447", "point down|look below"),
        ("\U0000261D", "index up"), ("\U0000270B", "hand|stop wait high five"), ("\U0001F91A", "back hand"),
        ("\U0001F596", "vulcan|spock"), ("\U0001F918", "rock|metal"), ("\U0001F919", "call me|phone"),
        ("\U0001F90C", "pinched"), ("\U0001F90F", "pinch|small tiny"), ("\U0000270C", "peace|victory"),
        ("\U0001F91E", "crossed fingers|luck hope"), ("\U0001F91F", "love you"), ("\U0001F440", "eyes|look see watch"),
        ("\U0001F441", "eye|see watch"), ("\U0001F9E0", "brain|smart think idea"), ("\U0001F5E3", "speaking head|talk say"),
        // Hearts & Symbols
        ("\U00002764", "heart|love red"), ("\U0001F9E1", "orange heart|love"), ("\U0001F49B", "yellow heart|love"),
        ("\U0001F49A", "green heart|love"), ("\U0001F499", "blue heart|love"), ("\U0001F49C", "purple heart|love"),
        ("\U0001F5A4", "black heart|love dark"), ("\U0001F90D", "white heart|love"), ("\U0001F494", "broken heart|sad"),
        ("\U0001F495", "two hearts|love"), ("\U0001F496", "sparkling heart|love"), ("\U0001F4AF", "100|perfect score"),
        ("\U0001F4A5", "boom|explosion bang"), ("\U0001F4A2", "anger|mad"), ("\U0001F4AB", "dizzy star"),
        ("\U0001F4AC", "speech bubble|talk chat message"), ("\U0001F4AD", "thought bubble|think idea"),
        ("\U00002705", "check|yes done correct complete"), ("\U0000274C", "cross mark|no wrong error delete"), ("\U00002753", "question|help why"),
        ("\U00002757", "exclamation|important alert"), ("\U000026A0", "warning|caution danger"), ("\U0001F6AB", "prohibited|ban forbidden no"),
        ("\U0001F6D1", "stop sign|halt"), ("\U0000267B", "recycle|environment green"), ("\U00002B50", "star|favorite rate"),
        ("\U0001F31F", "glowing star|sparkle shine"), ("\U00002728", "sparkles|magic new clean"), ("\U0001F525", "fire|hot lit trending popular"),
        ("\U0001F4A3", "bomb|explosive"), ("\U0001F4A1", "light bulb|idea tip hint"), ("\U0001F514", "bell|notification alert ring"),
        // Objects & Tools
        ("\U0001F50D", "magnifying glass|search find zoom"), ("\U0001F4CC", "pin|location mark save"), ("\U0001F4CB", "clipboard|copy paste"),
        ("\U0001F4DD", "memo|note write"), ("\U0001F4C1", "folder|file directory"), ("\U0001F4C2", "open folder|file"),
        ("\U0001F4C4", "document|page file"), ("\U0001F4C8", "chart up|graph growth increase"), ("\U0001F4C9", "chart down|graph decline decrease"),
        ("\U0001F4CA", "bar chart|data graph stats"), ("\U0001F4E7", "email|mail message"), ("\U0001F4E2", "loudspeaker|announce"),
        ("\U0001F4E3", "megaphone|announce"), ("\U0001F512", "lock|secure private"), ("\U0001F513", "unlock|open"),
        ("\U0001F511", "key|password access"), ("\U0001F527", "wrench|fix repair"), ("\U00002699", "gear|settings config"),
        ("\U0001F6E0", "tools|fix build"), ("\U0001F5D1", "trash|delete remove"), ("\U0001F4F7", "camera|photo screenshot"),
        ("\U0001F4F8", "camera flash|photo"), ("\U0001F3A5", "movie camera|video film"), ("\U0001F4F1", "phone|mobile"),
        ("\U0001F4BB", "laptop|computer"), ("\U0001F5A5", "desktop|computer monitor"), ("\U00002328", "keyboard|type"),
        ("\U0001F5A8", "printer|print"), ("\U0001F50B", "battery|power energy"), ("\U0001F50C", "plug|electric power"),
        ("\U000023F0", "alarm clock|time wake"), ("\U0000231A", "watch|time"), ("\U0001F4B0", "money bag|rich cash"),
        ("\U0001F4B3", "credit card|payment"), ("\U0001F4E6", "package|box delivery"), ("\U0001F381", "gift|present birthday"),
        ("\U0001F3AF", "target|goal aim bullseye"), ("\U0001F3C6", "trophy|winner champion"), ("\U0001F396", "medal|award"),
        // Nature & Animals
        ("\U0001F436", "dog"), ("\U0001F431", "cat"), ("\U0001F42D", "mouse"),
        ("\U0001F430", "rabbit"), ("\U0001F43B", "bear"), ("\U0001F43C", "panda"),
        ("\U0001F414", "chicken"), ("\U0001F41B", "bug"), ("\U0001F41D", "bee"),
        ("\U0001F40D", "snake"), ("\U0001F422", "turtle"), ("\U0001F419", "octopus"),
        ("\U0001F988", "shark"), ("\U0001F984", "unicorn"), ("\U0001F409", "dragon"),
        ("\U0001F332", "tree"), ("\U0001F333", "tree2"), ("\U0001F335", "cactus"),
        ("\U0001F339", "rose"), ("\U0001F33B", "sunflower"), ("\U0001F340", "four leaf"),
        ("\U0001F30D", "globe"), ("\U0001F30E", "americas"), ("\U0001F30F", "asia"),
        // Food & Drink
        ("\U0001F34E", "apple"), ("\U0001F34F", "green apple"), ("\U0001F353", "strawberry"),
        ("\U0001F352", "cherry"), ("\U0001F349", "watermelon"), ("\U0001F34C", "banana"),
        ("\U0001F355", "pizza"), ("\U0001F354", "burger"), ("\U0001F35F", "fries"),
        ("\U0001F32E", "taco"), ("\U0001F370", "cake"), ("\U0001F36D", "lollipop"),
        ("\U00002615", "coffee"), ("\U0001F37A", "beer"), ("\U0001F377", "wine"),
        // Travel & Transport
        ("\U0001F680", "rocket"), ("\U00002708", "airplane"), ("\U0001F697", "car"),
        ("\U0001F695", "taxi"), ("\U0001F6B2", "bicycle"), ("\U0001F3E0", "house"),
        ("\U0001F3E2", "office"), ("\U0001F3D7", "construction"), ("\U0001F5FC", "tower"),
        // Activities & Celebration
        ("\U0001F389", "party"), ("\U0001F38A", "confetti"), ("\U0001F388", "balloon"),
        ("\U0001F3B5", "music note"), ("\U0001F3B6", "notes"), ("\U0001F3A8", "palette"),
        ("\U0001F3AE", "gaming"), ("\U0001F3B2", "dice"), ("\U0001F504", "arrows"),
        // Flags & misc
        ("\U0001F6A8", "alert"), ("\U0001F4A8", "dash"), ("\U0001F4A4", "zzz sleep"),
        ("\U0001F573", "hole"), ("\U0001F648", "see no evil"), ("\U0001F649", "hear no evil"),
        ("\U0001F64A", "speak no evil"), ("\U0001F4F0", "newspaper"), ("\U0001F5DE", "rolled newspaper"),
    };
}
