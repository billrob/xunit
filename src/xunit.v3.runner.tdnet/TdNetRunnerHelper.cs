using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using TestDriven.Framework;
using Xunit.Internal;
using Xunit.Runner.Common;
using Xunit.Runner.v2;
using Xunit.Sdk;
using Xunit.v3;

namespace Xunit.Runner.TdNet
{
	public class TdNetRunnerHelper : IAsyncDisposable
	{
		readonly TestAssemblyConfiguration configuration = new();
		bool disposed;
		readonly DisposalTracker disposalTracker = new DisposalTracker();
		readonly ITestListener? testListener;
		readonly Xunit2? xunit;

		/// <summary>
		/// This constructor is for unit testing purposes only.
		/// </summary>
		protected TdNetRunnerHelper()
		{ }

		public TdNetRunnerHelper(
			Assembly assembly,
			ITestListener testListener)
		{
			this.testListener = testListener;

			var assemblyFileName = assembly.GetLocalCodeBase();
			ConfigReader.Load(configuration, assemblyFileName);
			var diagnosticMessageSink = new DiagnosticMessageSink(testListener, Path.GetFileNameWithoutExtension(assemblyFileName), configuration.DiagnosticMessagesOrDefault);
			xunit = new Xunit2(diagnosticMessageSink, configuration.AppDomainOrDefault, _NullSourceInformationProvider.Instance, assemblyFileName, shadowCopy: false);
			disposalTracker.Add(xunit);
		}

		public virtual IReadOnlyList<_TestCaseDiscovered> Discover()
		{
			Guard.NotNull($"Attempted to use an uninitialized {GetType().FullName}", xunit);

			var settings = new FrontControllerFindSettings(_TestFrameworkOptions.ForDiscovery(configuration));
			return Discover(sink => xunit.Find(sink, settings));
		}

		IReadOnlyList<_TestCaseDiscovered> Discover(Type? type)
		{
			Guard.NotNull($"Attempted to use an uninitialized {GetType().FullName}", xunit);

			if (type == null || type.FullName == null)
				return new _TestCaseDiscovered[0];

			var settings = new FrontControllerFindSettings(_TestFrameworkOptions.ForDiscovery(configuration));
			settings.Filters.IncludedClasses.Add(type.FullName);

			return Discover(sink => xunit.Find(sink, settings));
		}

		IReadOnlyList<_TestCaseDiscovered> Discover(Action<_IMessageSink> discoveryAction)
		{
			try
			{
				var sink = new TestDiscoverySink();
				disposalTracker.Add(sink);
				discoveryAction(sink);
				sink.Finished.WaitOne();
				return sink.TestCases.ToList();
			}
			catch (Exception ex)
			{
				testListener?.WriteLine("Error during test discovery:\r\n" + ex, Category.Error);
				return new _TestCaseDiscovered[0];
			}
		}

		public ValueTask DisposeAsync()
		{
			if (disposed)
				throw new ObjectDisposedException(GetType().FullName);

			disposed = true;

			return disposalTracker.DisposeAsync();
		}

		public virtual TestRunState Run(
			IReadOnlyList<_TestCaseDiscovered>? testCases = null,
			TestRunState initialRunState = TestRunState.NoTests)
		{
			Guard.NotNull($"Attempted to use an uninitialized {GetType().FullName}", testListener);
			Guard.NotNull($"Attempted to use an uninitialized {GetType().FullName}", xunit);

			try
			{
				if (testCases == null)
					testCases = Discover();

				var resultSink = new ResultSink(testListener, testCases.Count) { TestRunState = initialRunState };
				disposalTracker.Add(resultSink);

				var executionOptions = _TestFrameworkOptions.ForExecution(configuration);
				xunit.RunTests(testCases.Select(tc => tc.Serialization), resultSink, executionOptions);

				resultSink.Finished.WaitOne();

				return resultSink.TestRunState;
			}
			catch (Exception ex)
			{
				testListener.WriteLine("Error during test execution:\r\n" + ex, Category.Error);
				return TestRunState.Error;
			}
		}

		public virtual TestRunState RunClass(
			Type type,
			TestRunState initialRunState = TestRunState.NoTests)
		{
			var state = Run(Discover(type), initialRunState);

			foreach (var memberInfo in type.GetMembers())
			{
				var childType = memberInfo as Type;
				if (childType != null)
					state = RunClass(childType, state);
			}

			return state;
		}

		public virtual TestRunState RunMethod(
			MethodInfo method,
			TestRunState initialRunState = TestRunState.NoTests)
		{
			var testCases = Discover(method.ReflectedType).Where(tc =>
			{
				if (tc.TestClassWithNamespace == null || tc.TestMethod == null)
					return false;

				var typeInfo = Type.GetType(tc.TestClassWithNamespace);
				if (typeInfo == null)
					return false;

				var methodInfo = typeInfo.GetMethod(tc.TestMethod, BindingFlags.Public);
				if (methodInfo == null)
					return false;

				if (methodInfo == method)
					return true;

				if (methodInfo.IsGenericMethod)
					return methodInfo.GetGenericMethodDefinition() == method;

				return false;
			}).ToList();

			return Run(testCases, initialRunState);
		}
	}
}
