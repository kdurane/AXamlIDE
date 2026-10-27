using System;
using System.Linq;
using System.Xml.Linq;
using AXamlIDE.Services.Testing;
using Xunit;

namespace AXamlIDE.Tests;

/// <summary>The Test Explorer's Core parts (its own tests are named so they don't read as "tests of tests").</summary>
public class TestingTests
{
    [Fact]
    public void Listed_names_split_into_class_method_and_arguments()
    {
        var name = TestCaseName.Parse("My.Ns.Calc.Adds(a: 1.5, b: \"x.y\")");
        Assert.Equal("My.Ns.Calc", name.ClassName);
        Assert.Equal("Adds", name.MethodName);
        Assert.Equal("(a: 1.5, b: \"x.y\")", name.Arguments);
        Assert.Equal("My.Ns.Calc.Adds", name.MethodFullName);

        var bare = TestCaseName.Parse("JustAMethod");
        Assert.Equal("", bare.ClassName);
        Assert.Equal("JustAMethod", bare.MethodFullName);
    }

    [Fact]
    public void The_test_list_is_read_from_after_its_heading()
    {
        string[] output =
        [
            "  Determining projects to restore...",
            "  App.Tests -> /x/App.Tests.dll",
            "The following Tests are available:",
            "    App.Tests.A.One",
            "    App.Tests.A.Two(x: 1)",
            "    App.Tests.A.One",   // a second target framework lists it again
            "Some later line"
        ];

        var tests = DotNetTestRunner.ParseTestList(output);

        Assert.Equal(["App.Tests.A.One", "App.Tests.A.Two(x: 1)"], tests.Select(test => test.FullName));
    }

    [Fact]
    public void Trx_results_have_their_class_outcome_and_failure()
    {
        const string trx = """
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                <UnitTestResult testId="1" testName="Ns.Calc.Adds(a: 1)" outcome="Passed" duration="00:00:00.0100000" />
                <UnitTestResult testId="2" testName="Ns.Calc.Fails" outcome="Failed" duration="00:00:01">
                  <Output>
                    <StdOut>printed</StdOut>
                    <ErrorInfo><Message>Assert.Equal() Failure</Message><StackTrace>at Ns.Calc.Fails()</StackTrace></ErrorInfo>
                  </Output>
                </UnitTestResult>
                <UnitTestResult testId="3" testName="Skipped" outcome="NotExecuted" />
              </Results>
              <TestDefinitions>
                <UnitTest id="1"><TestMethod className="Ns.Calc" name="Adds" /></UnitTest>
                <UnitTest id="2"><TestMethod className="Ns.Calc" name="Fails" /></UnitTest>
                <UnitTest id="3"><TestMethod className="Ns.Other" name="Skipped" /></UnitTest>
              </TestDefinitions>
            </TestRun>
            """;

        var results = TrxReader.Read(XDocument.Parse(trx));

        Assert.Equal([TestOutcome.Passed, TestOutcome.Failed, TestOutcome.Skipped], results.Select(r => r.Outcome));
        Assert.Equal(new TestCaseName("Ns.Calc.Adds(a: 1)", "Ns.Calc", "Adds", "(a: 1)"), results[0].Name);
        Assert.Equal(TimeSpan.FromSeconds(1), results[1].Duration);
        Assert.Equal("Assert.Equal() Failure", results[1].ErrorMessage);
        Assert.Equal("at Ns.Calc.Fails()", results[1].StackTrace);
        Assert.Equal("printed", results[1].Output);
        Assert.Equal("Ns.Other", results[2].Name.ClassName);   // MSTest: the result's name is just the method
    }

    [Fact]
    public void Filters_name_each_method_once_and_escape_specials()
    {
        var filter = TestFilter.ForMethods(
        [
            TestCaseName.Parse("Ns.C.M(x: 1)"),
            TestCaseName.Parse("Ns.C.M(x: 2)"),
            TestCaseName.Parse("Bare")
        ]);

        Assert.Equal("FullyQualifiedName=Ns.C.M|Name=Bare", filter);
        Assert.Equal(@"a\(b\)\|c\=\!\~\&\\", TestFilter.Escape(@"a(b)|c=!~&\"));
        Assert.Equal("FullyQualifiedName~Ns.C.", TestFilter.ForPrefix("Ns.C"));
        Assert.Null(TestFilter.ForMethods([]));
    }

    [Theory]
    [InlineData("""<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="xunit" Version="2" /></ItemGroup></Project>""", true)]
    [InlineData("""<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" /></ItemGroup></Project>""", true)]
    [InlineData("""<Project Sdk="MSTest.Sdk/3.6.0"></Project>""", true)]
    [InlineData("""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup></Project>""", true)]
    [InlineData("""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><IsTestProject>false</IsTestProject></PropertyGroup><ItemGroup><PackageReference Include="xunit" /></ItemGroup></Project>""", false)]
    [InlineData("""<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Avalonia" /></ItemGroup></Project>""", false)]
    [InlineData("not xml", false)]
    public void Test_projects_are_recognised(string projectXml, bool isTestProject)
        => Assert.Equal(isTestProject, TestProjectDetector.IsTestProject(projectXml));

    [Fact]
    public void Stack_trace_lines_link_to_their_source()
    {
        var frames = StackTraceFrames.Parse("""
               at Ns.Calc.Fails() in /src/Calc Tests.cs:line 42
               at System.RuntimeMethodHandle.InvokeMethod(Object target)
               at Ns.Win.Run() in C:\src\Win.cs:line 7
            """);

        Assert.Equal(3, frames.Count);
        Assert.Equal(("/src/Calc Tests.cs", 42), (frames[0].Path, frames[0].Line));
        Assert.Null(frames[1].Path);
        Assert.Equal((@"C:\src\Win.cs", 7), (frames[2].Path, frames[2].Line));
        Assert.Equal("at System.RuntimeMethodHandle.InvokeMethod(Object target)", frames[1].Text);
    }

    [Fact]
    public void A_test_method_is_found_in_its_class()
    {
        const string source = """
            namespace Ns;

            public class Other
            {
                public void Adds() { }
            }

            public class Calc
            {
                private int AddsHelper() => 1;

                [Fact]
                public void Adds ()
                {
                    Adds2();
                }
            }
            """;

        Assert.Equal((13, 16), TestSourceLocator.FindInText(source, "Ns.Calc", "Adds"));
        Assert.Equal((5, 16), TestSourceLocator.FindInText(source, "", "Adds"));
        Assert.Null(TestSourceLocator.FindInText(source, "Ns.Missing", "Adds"));
    }
}
