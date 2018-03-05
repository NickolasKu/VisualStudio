using System.ComponentModel.Composition;
using GitHub.Models;
using Octokit;

namespace GitHub.VisualStudio
{
    [Export(typeof(IGitHubClient))]
    public class GHClient : GitHubClient
    {
        [ImportingConstructor]
        public GHClient(IProgram program)
            : base(program.ProductHeader)
        {
        }
    }
}
