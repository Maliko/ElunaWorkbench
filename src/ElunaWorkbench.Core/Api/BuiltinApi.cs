namespace ElunaWorkbench.Core.Api;

/// <summary>
/// Hand written API definitions: the Lua standard library (5.1 – 5.4 / LuaJIT),
/// the AIO API (https://github.com/Rochet2/AIO) and a small subset of the WoW
/// client API that is typically used inside AIO client addons.
///
/// Signature mini language: <c>name(a: type, b?: type, ...): returnType | description</c>.
/// A '?' marks an optional parameter.
/// </summary>
internal static class BuiltinApi
{
    public static readonly string[] LuaGlobals =
    {
        "assert(v: any, message?: string): any | Raises an error if v is false or nil; otherwise returns all its arguments.",
        "collectgarbage(opt?: string, arg?: number): any | Generic interface to the garbage collector.",
        "dofile(filename?: string): any | Opens the named file and executes its contents as a Lua chunk.",
        "error(message: any, level?: number) | Terminates the last protected function called and returns message as the error object.",
        "getmetatable(object: any): table | Returns the metatable of the given object.",
        "ipairs(t: table): function | Iterates over the pairs (1,t[1]), (2,t[2]), ... up to the first nil value.",
        "load(chunk: any, chunkname?: string, mode?: string, env?: table): function | Loads a chunk.",
        "loadfile(filename?: string, mode?: string, env?: table): function | Similar to load, but gets the chunk from a file.",
        "loadstring(string: string, chunkname?: string): function | Lua 5.1: loads a chunk from the given string.",
        "next(table: table, index?: any): any | Allows a program to traverse all fields of a table.",
        "pairs(t: table): function | Iterates over all key-value pairs of table t.",
        "pcall(f: function, ...): boolean | Calls function f in protected mode.",
        "print(...) | Prints its arguments to the server console (stdout).",
        "rawequal(v1: any, v2: any): boolean | Checks whether v1 is equal to v2, without invoking any metamethod.",
        "rawget(table: table, index: any): any | Gets the real value of table[index], without invoking any metamethod.",
        "rawlen(v: any): number | Returns the length of the object v, without invoking any metamethod.",
        "rawset(table: table, index: any, value: any): table | Sets the real value of table[index] to value.",
        "require(modname: string): any | Loads the given module.",
        "select(index: any, ...): any | Returns all arguments after argument number index, or the count when index is \"#\".",
        "setmetatable(table: table, metatable?: table): table | Sets the metatable for the given table.",
        "tonumber(e: any, base?: number): number | Tries to convert its argument to a number.",
        "tostring(v: any): string | Converts a value of any type to a string.",
        "type(v: any): string | Returns the type of its only argument, coded as a string.",
        "unpack(list: table, i?: number, j?: number): any | Lua 5.1: returns the elements from the given table.",
        "xpcall(f: function, msgh: function, ...): boolean | Calls function f in protected mode with a message handler.",
        "getfenv(f?: any): table | Lua 5.1: returns the current environment in use by the function.",
        "setfenv(f: any, table: table): function | Lua 5.1: sets the environment to be used by the given function.",
        "module(name: string, ...) | Lua 5.1: creates a module.",
    };

    public static readonly (string Name, string Description, string[] Functions)[] LuaLibraries =
    {
        ("string", "String manipulation library.", new[]
        {
            "byte(s: string, i?: number, j?: number): number | Returns the internal numeric codes of the characters.",
            "char(...): string | Receives zero or more integers and returns a string.",
            "dump(function: function, strip?: boolean): string | Returns a string containing a binary representation of the function.",
            "find(s: string, pattern: string, init?: number, plain?: boolean): number | Looks for the first match of pattern in the string s.",
            "format(formatstring: string, ...): string | Returns a formatted version of its variable number of arguments.",
            "gmatch(s: string, pattern: string): function | Returns an iterator function over all matches of pattern.",
            "gsub(s: string, pattern: string, repl: any, n?: number): string | Global substitution of pattern matches.",
            "len(s: string): number | Returns the length of the string.",
            "lower(s: string): string | Returns a copy of the string with all uppercase letters changed to lowercase.",
            "match(s: string, pattern: string, init?: number): string | Looks for the first match of pattern in the string s.",
            "rep(s: string, n: number, sep?: string): string | Returns a string that is the concatenation of n copies of s.",
            "reverse(s: string): string | Returns the string reversed.",
            "sub(s: string, i: number, j?: number): string | Returns the substring of s from i to j.",
            "upper(s: string): string | Returns a copy of the string with all lowercase letters changed to uppercase.",
            "pack(fmt: string, ...): string | Lua 5.3+: packs values into a binary string.",
            "unpack(fmt: string, s: string, pos?: number): any | Lua 5.3+: unpacks a binary string.",
            "packsize(fmt: string): number | Lua 5.3+: size of a string resulting from string.pack.",
        }),
        ("table", "Table manipulation library.", new[]
        {
            "concat(list: table, sep?: string, i?: number, j?: number): string | Concatenates the elements of a list.",
            "insert(list: table, pos: any, value?: any) | Inserts element value at position pos in list.",
            "remove(list: table, pos?: number): any | Removes from list the element at position pos.",
            "sort(list: table, comp?: function) | Sorts list elements in-place.",
            "unpack(list: table, i?: number, j?: number): any | Lua 5.2+: returns the elements from the given list.",
            "pack(...): table | Lua 5.2+: returns a new table with all arguments stored into keys 1, 2, etc. and field n.",
            "move(a1: table, f: number, e: number, t: number, a2?: table): table | Lua 5.3+: moves elements between tables.",
            "maxn(table: table): number | Lua 5.1: returns the largest positive numerical index.",
            "getn(table: table): number | Lua 5.0 compat: returns the size of a table.",
        }),
        ("math", "Mathematical functions.", new[]
        {
            "abs(x: number): number | Absolute value.", "acos(x: number): number | Arc cosine.", "asin(x: number): number | Arc sine.",
            "atan(y: number, x?: number): number | Arc tangent.", "atan2(y: number, x: number): number | Lua 5.1: arc tangent of y/x.",
            "ceil(x: number): number | Smallest integer >= x.", "cos(x: number): number | Cosine.", "cosh(x: number): number | Hyperbolic cosine.",
            "deg(x: number): number | Radians to degrees.", "exp(x: number): number | e^x.", "floor(x: number): number | Largest integer <= x.",
            "fmod(x: number, y: number): number | Remainder of x / y rounding towards zero.", "frexp(x: number): number | Splits x into mantissa and exponent.",
            "ldexp(m: number, e: number): number | m * 2^e.", "log(x: number, base?: number): number | Logarithm.", "log10(x: number): number | Base-10 logarithm.",
            "max(x: number, ...): number | Maximum value.", "min(x: number, ...): number | Minimum value.", "modf(x: number): number | Integral and fractional parts.",
            "pow(x: number, y: number): number | x^y.", "rad(x: number): number | Degrees to radians.",
            "random(m?: number, n?: number): number | Pseudo-random number.", "randomseed(x?: number) | Seeds the pseudo-random generator.",
            "sin(x: number): number | Sine.", "sinh(x: number): number | Hyperbolic sine.", "sqrt(x: number): number | Square root.",
            "tan(x: number): number | Tangent.", "tanh(x: number): number | Hyperbolic tangent.",
            "tointeger(x: any): number | Lua 5.3+: converts to integer.", "type(x: any): string | Lua 5.3+: \"integer\", \"float\" or nil.",
            "ult(m: number, n: number): boolean | Lua 5.3+: unsigned comparison.",
        }),
        ("os", "Operating system facilities.", new[]
        {
            "clock(): number | CPU time used by the program in seconds.", "date(format?: string, time?: number): any | Formatted date.",
            "difftime(t2: number, t1: number): number | Difference in seconds.", "execute(command?: string): any | Executes a shell command.",
            "exit(code?: any, close?: boolean) | Terminates the host program.", "getenv(varname: string): string | Value of an environment variable.",
            "remove(filename: string): boolean | Deletes a file.", "rename(oldname: string, newname: string): boolean | Renames a file.",
            "setlocale(locale?: string, category?: string): string | Sets the current locale.", "time(table?: table): number | Current time.",
            "tmpname(): string | Name for a temporary file.",
        }),
        ("io", "Input and output facilities.", new[]
        {
            "close(file?: any) | Closes file.", "flush() | Flushes the default output file.", "input(file?: any): any | Default input file.",
            "lines(filename?: string, ...): function | Iterator over the lines of a file.", "open(filename: string, mode?: string): any | Opens a file.",
            "output(file?: any): any | Default output file.", "popen(prog: string, mode?: string): any | Starts a program.",
            "read(...): any | Reads from the default input file.", "tmpfile(): any | Opens a temporary file.", "type(obj: any): string | Checks whether obj is a file handle.",
            "write(...): any | Writes to the default output file.",
        }),
        ("coroutine", "Coroutine manipulation.", new[]
        {
            "create(f: function): any | Creates a new coroutine.", "isyieldable(): boolean | Lua 5.3+: whether the running coroutine can yield.",
            "resume(co: any, ...): boolean | Starts or continues a coroutine.", "running(): any | Returns the running coroutine.",
            "status(co: any): string | Status of a coroutine.", "wrap(f: function): function | Creates a coroutine wrapped in a function.",
            "yield(...): any | Suspends the running coroutine.", "close(co: any): boolean | Lua 5.4: closes a coroutine.",
        }),
        ("debug", "Debug library.", new[]
        {
            "debug() | Enters interactive debug mode.", "gethook(thread?: any): any | Current hook settings.", "getinfo(f: any, what?: string): table | Information about a function.",
            "getlocal(f: any, local: number): any | Name and value of a local variable.", "getmetatable(value: any): table | Metatable of value.",
            "getupvalue(f: function, up: number): any | Name and value of an upvalue.", "sethook(hook: function, mask: string, count?: number) | Sets a debug hook.",
            "setlocal(level: number, local: number, value: any): string | Assigns a local variable.", "setmetatable(value: any, table: table): any | Sets the metatable.",
            "setupvalue(f: function, up: number, value: any): string | Assigns an upvalue.", "traceback(message?: any, level?: number): string | Stack traceback.",
        }),
        ("bit32", "Lua 5.2 bitwise operations.", new[]
        {
            "arshift(x: number, disp: number): number | Arithmetic shift.", "band(...): number | Bitwise and.", "bnot(x: number): number | Bitwise not.",
            "bor(...): number | Bitwise or.", "btest(...): boolean | Whether the bitwise and is not zero.", "bxor(...): number | Bitwise exclusive or.",
            "extract(n: number, field: number, width?: number): number | Extracts bits.", "replace(n: number, v: number, field: number, width?: number): number | Replaces bits.",
            "lrotate(x: number, disp: number): number | Left rotation.", "lshift(x: number, disp: number): number | Left shift.",
            "rrotate(x: number, disp: number): number | Right rotation.", "rshift(x: number, disp: number): number | Logical right shift.",
        }),
        ("bit", "LuaJIT BitOp library.", new[]
        {
            "tobit(x: number): number | Normalizes a number to the numeric range for bit operations.", "tohex(x: number, n?: number): string | Hex string.",
            "bnot(x: number): number | Bitwise not.", "band(x: number, ...): number | Bitwise and.", "bor(x: number, ...): number | Bitwise or.",
            "bxor(x: number, ...): number | Bitwise xor.", "lshift(x: number, n: number): number | Left shift.", "rshift(x: number, n: number): number | Logical right shift.",
            "arshift(x: number, n: number): number | Arithmetic right shift.", "rol(x: number, n: number): number | Left rotation.",
            "ror(x: number, n: number): number | Right rotation.", "bswap(x: number): number | Swaps the bytes.",
        }),
        ("utf8", "Lua 5.3+ UTF-8 support.", new[]
        {
            "char(...): string | Converts code points to a string.", "codes(s: string): function | Iterator over all characters.",
            "codepoint(s: string, i?: number, j?: number): number | Code points of characters.", "len(s: string, i?: number, j?: number): number | Number of UTF-8 characters.",
            "offset(s: string, n: number, i?: number): number | Byte position of a character.",
        }),
    };

    public static readonly (string Library, string Name, string Type, string Description)[] LuaFields =
    {
        ("", "_G", "table", "The global environment table."),
        ("", "_VERSION", "string", "The running Lua version, e.g. \"Lua 5.2\"."),
        ("", "_ENV", "table", "Lua 5.2+: the current environment."),
        ("", "arg", "table", "Command line arguments."),
        ("math", "pi", "number", "The value of π."),
        ("math", "huge", "number", "A value larger than any other numeric value."),
        ("math", "maxinteger", "number", "Lua 5.3+: maximum integer value."),
        ("math", "mininteger", "number", "Lua 5.3+: minimum integer value."),
        ("io", "stdin", "any", "Standard input."),
        ("io", "stdout", "any", "Standard output."),
        ("io", "stderr", "any", "Standard error."),
        ("package", "path", "string", "The path used by require to search for a Lua loader."),
        ("package", "cpath", "string", "The path used by require to search for a C loader."),
        ("package", "loaded", "table", "Table of already loaded modules."),
        ("package", "preload", "table", "Table to store loaders for specific modules."),
        ("utf8", "charpattern", "string", "Pattern matching exactly one UTF-8 byte sequence."),
    };

    // -------------------------------------------------------------------------------------
    // AIO (https://github.com/Rochet2/AIO)
    // -------------------------------------------------------------------------------------
    public const string AioMessageClass = "AIOMsg";

    public static readonly string[] AioFunctions =
    {
        "IsServer(): boolean | Returns true if we are on server side, false if we are on client side.",
        "IsMainState(): boolean | Returns true if we are on the main Lua state (always true on client side).",
        "GetVersion(): number | Returns the AIO version. Note that the type is not guaranteed to be a number.",
        "AddAddon(path?: string, name?: string): boolean | Adds the file at the given path to the addons sent to players (server side, main state). Returns false on client side and true on server side. Typical usage at the top of an addon file: `if AIO.AddAddon() then return end`.",
        "AddAddonCode(name: string, code: string) | Server only: adds code as an addon that is sent to players. Call only on startup.",
        "Handle(player: Player, name: string, handlername?: string, ...) | Triggers the handler 'handlername' of the handler table registered with AIO.AddHandlers(name, ...) on the other side. Server version: AIO.Handle(player, name, handlername, ...). Client version: AIO.Handle(name, handlername, ...).",
        "AddHandlers(name: string, handlertable: table): table | Main state only: registers a table of handler functions for 'name'. Handlers are called as handler(player, ...). Returns the passed table.",
        "RegisterEvent(name: string, func: function) | Main state only: adds a callback that is called when a message with the given name is received.",
        "AddOnInit(func: function) | Server only: adds a function called when the initial message is sent to the player: func(msg, player).",
        "AddSavedVar(key: string) | Client only: the global _G[key] is saved account bound between sessions.",
        "AddSavedVarChar(key: string) | Client only: the global _G[key] is saved character bound between sessions.",
        "SavePosition(frame: any, char?: boolean) | Client only: makes the frame save and restore its position.",
        "Msg(): AIOMsg | Creates and returns a new AIO message. Example: AIO.Msg():Add(\"MyHandlerName\", param1, param2):Send(player)",
    };

    public static readonly string[] AioMessageMethods =
    {
        "Add(name: string, ...): AIOMsg | Adds a message for the handler registered with 'name' on the receiving end. Returns self.",
        "Append(msg2: AIOMsg): AIOMsg | Appends another message to this one. Returns self.",
        "Send(player: Player, ...): AIOMsg | Sends the message. Server: to all passed players. Client: to the server (no arguments). Returns self.",
        "HasMsg(): boolean | Returns true if the message has something in it.",
        "ToString(): string | Returns the message as a string.",
        "Clear(): AIOMsg | Erases the message built so far. Returns self.",
        "Assemble(): AIOMsg | Assembles the message string. Mainly for internal use. Returns self.",
    };

    /// <summary>Small subset of the WoW client API commonly used in AIO client addons.</summary>
    public static readonly string[] WowClientGlobals =
    {
        "CreateFrame(frameType: string, name?: string, parent?: any, template?: string): any | Creates a new UI frame.",
        "UnitName(unit: string): string | Returns the name of the unit.",
        "UnitGUID(unit: string): string | Returns the GUID of the unit.",
        "UnitLevel(unit: string): number | Returns the level of the unit.",
        "UnitClass(unit: string): string | Returns the class of the unit.",
        "UnitRace(unit: string): string | Returns the race of the unit.",
        "UnitHealth(unit: string): number | Returns the current health of the unit.",
        "UnitHealthMax(unit: string): number | Returns the maximum health of the unit.",
        "UnitExists(unit: string): boolean | Returns whether the unit exists.",
        "GetTime(): number | Returns the system uptime in seconds.",
        "GetItemInfo(item: any): string | Returns information about an item.",
        "GetSpellInfo(spell: any): string | Returns information about a spell.",
        "PlaySound(sound: any) | Plays a sound.",
        "ReloadUI() | Reloads the user interface.",
        "hooksecurefunc(table: any, functionName: any, hookfunc?: function) | Securely post-hooks a function.",
        "InterfaceOptions_AddCategory(panel: any) | Adds a panel to the interface options.",
        "StaticPopup_Show(which: string, text1?: string, text2?: string, data?: any): any | Shows a static popup.",
        "SendChatMessage(msg: string, chatType?: string, language?: any, channel?: any) | Sends a chat message.",
        "SendAddonMessage(prefix: string, text: string, type: string, target?: string) | Sends an addon message.",
        "tinsert(t: table, pos: any, value?: any) | Alias of table.insert.",
        "tremove(t: table, pos?: number): any | Alias of table.remove.",
        "strsplit(delimiter: string, str: string, pieces?: number): string | Splits a string.",
        "strjoin(delimiter: string, ...): string | Joins strings.",
        "strtrim(str: string, chars?: string): string | Trims a string.",
        "format(formatstring: string, ...): string | Alias of string.format.",
        "wipe(t: table): table | Removes all entries from a table.",
    };

    public static readonly (string Name, string Type, string Description)[] WowClientFields =
    {
        ("UIParent", "any", "The root UI frame."),
        ("WorldFrame", "any", "The 3D world frame."),
        ("GameTooltip", "any", "The default game tooltip."),
        ("DEFAULT_CHAT_FRAME", "any", "The default chat frame."),
        ("SlashCmdList", "table", "Table of slash command handlers."),
        ("StaticPopupDialogs", "table", "Table of static popup definitions."),
        ("UISpecialFrames", "table", "Frames closed with Escape."),
    };
}
