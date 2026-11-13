// SPDX-License-Identifier: MIT
// The marker interfaces of the generated game types live in Kiln.Api since 0.2.0-dev.27 (Kiln.Api/GameStructs.cs); a mod
// built against an older Kiln.Game still finds them through these forwards.
using System.Runtime.CompilerServices;

[assembly: TypeForwardedTo(typeof(Kiln.IGameStruct<>))]
[assembly: TypeForwardedTo(typeof(Kiln.IGameComponent<>))]
[assembly: TypeForwardedTo(typeof(Kiln.IGameEvent<>))]
[assembly: TypeForwardedTo(typeof(Kiln.IGameAttribute<,>))]
