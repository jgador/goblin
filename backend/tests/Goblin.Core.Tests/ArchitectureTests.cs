using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Goblin.Core.Work;
using Xunit;

namespace Goblin.Core.Tests;

public sealed class ArchitectureTests
{
    [Fact]
    public void ProductRulesHaveNoRuntimeHttpMessagingOrPersistenceDependencies()
    {
        string[] dependencies = [.. typeof(WorkItem).Assembly.GetReferencedAssemblies().Select(x => x.Name!)];
        Assert.All(dependencies, name => Assert.True(name == "System.Runtime" || name == "System.Collections" ||
            name == "System.Private.CoreLib", "Unexpected core dependency: " + name));

        // Catch unused references too: they may not appear in emitted metadata.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "backend/Goblin.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        XDocument project = XDocument.Load(Path.Combine(directory.FullName, "backend/src/Goblin.Core/Goblin.Core.csproj"));
        Assert.DoesNotContain(project.Descendants(), x => x.Name.LocalName is "PackageReference" or "ProjectReference" or "FrameworkReference");
    }
}
