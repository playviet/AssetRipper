// mscorlib-shim - the assembly `mscorlib` that a netstandard2.1 compile of a recovered Unity project
// does not have, and that its PLUGIN dlls all reference.
//
// Every assembly in a Unity player build is compiled against Unity's `mscorlib`. A plugin dll that
// declares, in its own metadata, a type from `[mscorlib]System.*` - as a base type, an implemented
// interface, a parameter or a return - forces the C# compiler to resolve that typeref when it reads the
// plugin. Against a netstandard2.1 reference set there is no assembly with the identity `mscorlib` at all,
// so the resolve fails and the compiler emits
//
//     CS7069: Reference to type 'X' claims it is defined in 'mscorlib', but it could not be found
//
// at every *source* line that touches the plugin type. The error is entirely an artifact of the reference
// set: nothing is wrong with the recovered C#, and the Unity editor - which compiles against Unity's own
// mscorlib - never reports it. `ANYVERIFY.md` recorded 6 of these as Fluffy Field's "known floor"; they are
// not a floor, they are a missing reference.
//
// The fix is an assembly NAMED mscorlib that declares the handful of types the plugins reach for. It is not
// Unity's mscorlib: shipping that would put a second System.Object into the compilation and make the
// compiler choose a corlib. This declares only leaf types, no System.Object, so it is never a corlib
// candidate - it exists purely so a typeref that says "mscorlib" resolves to something with the right shape.
//
// The shapes must match the real ones, because the plugin's own members are typed in terms of them. They are
// copied from the .NET reference source; netstandard2.1 declares the same types, but under the identity
// `netstandard`, and an identity is what the plugin's typeref names.
//
// Add a type here when a new CS7069 names one. Keep them exact.

namespace System.Threading.Tasks.Sources
{
	public enum ValueTaskSourceStatus { Pending = 0, Succeeded = 1, Faulted = 2, Canceled = 3 }

	[Flags]
	public enum ValueTaskSourceOnCompletedFlags { None = 0, UseSchedulingContext = 1, FlowExecutionContext = 2 }

	public interface IValueTaskSource
	{
		ValueTaskSourceStatus GetStatus(short token);
		void OnCompleted(Action<object> continuation, object state, short token, ValueTaskSourceOnCompletedFlags flags);
		void GetResult(short token);
	}

	public interface IValueTaskSource<out TResult>
	{
		ValueTaskSourceStatus GetStatus(short token);
		void OnCompleted(Action<object> continuation, object state, short token, ValueTaskSourceOnCompletedFlags flags);
		TResult GetResult(short token);
	}
}

namespace System.Buffers
{
	public interface IBufferWriter<T>
	{
		void Advance(int count);
		Memory<T> GetMemory(int sizeHint = 0);
		Span<T> GetSpan(int sizeHint = 0);
	}
}
