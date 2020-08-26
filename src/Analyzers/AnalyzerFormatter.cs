﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the MIT license.  See License.txt in the project root for license information.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Tools.Formatters;
using Microsoft.Extensions.Logging;

namespace Microsoft.CodeAnalysis.Tools.Analyzers
{
    internal class AnalyzerFormatter : ICodeFormatter
    {
        private static readonly ImmutableArray<string> s_supportedLanguages = ImmutableArray.Create(LanguageNames.CSharp, LanguageNames.VisualBasic);

        private readonly string _name;
        private readonly IAnalyzerInformationProvider _informationProvider;
        private readonly IAnalyzerRunner _runner;
        private readonly ICodeFixApplier _applier;

        public FixCategory Category { get; }

        public AnalyzerFormatter(
            string name,
            FixCategory category,
            IAnalyzerInformationProvider informationProvider,
            IAnalyzerRunner runner,
            ICodeFixApplier applier)
        {
            _name = name;
            Category = category;
            _informationProvider = informationProvider;
            _runner = runner;
            _applier = applier;
        }

        public async Task<Solution> FormatAsync(
            Solution solution,
            ImmutableArray<DocumentId> formattableDocuments,
            FormatOptions formatOptions,
            ILogger logger,
            List<FormattedFile> formattedFiles,
            CancellationToken cancellationToken)
        {
            var (analyzers, fixers) = _informationProvider.GetAnalyzersAndFixers(solution, formatOptions, logger);
            if (analyzers.IsEmpty && fixers.IsEmpty)
            {
                return solution;
            }

            // Only include compiler diagnostics if we have a fixer that can fix them.
            var includeCompilerDiagnostics = fixers.Any(
                codefix => codefix.FixableDiagnosticIds.Any(
                    id => id.StartsWith("CS") || id.StartsWith("BC")));

            var analysisStopwatch = Stopwatch.StartNew();
            logger.LogTrace(Resources.Running_0_analysis, _name);

            var formattablePaths = formattableDocuments.Select(id => solution.GetDocument(id)!.FilePath)
                    .OfType<string>().ToImmutableHashSet();

            logger.LogTrace(Resources.Determining_diagnostics);

            var severity = _informationProvider.GetSeverity(formatOptions);

            // Filter to analyzers that report diagnostics with equal or greater severity.
            var projectAnalyzers = await FilterBySeverityAsync(solution.Projects, analyzers, formattablePaths, severity, cancellationToken).ConfigureAwait(false);

            // Determine which diagnostics are being reported for each project.
            var projectDiagnostics = await GetProjectDiagnosticsAsync(solution, projectAnalyzers, formattablePaths, formatOptions, severity, includeCompilerDiagnostics, logger, formattedFiles, cancellationToken).ConfigureAwait(false);

            var projectDiagnosticsMS = analysisStopwatch.ElapsedMilliseconds;
            logger.LogTrace(Resources.Complete_in_0_ms, projectDiagnosticsMS);

            logger.LogTrace(Resources.Fixing_diagnostics);

            // Run each analyzer individually and apply fixes if possible.
            solution = await FixDiagnosticsAsync(solution, analyzers, fixers, projectDiagnostics, formattablePaths, severity, includeCompilerDiagnostics, logger, cancellationToken).ConfigureAwait(false);

            var fixDiagnosticsMS = analysisStopwatch.ElapsedMilliseconds - projectDiagnosticsMS;
            logger.LogTrace(Resources.Complete_in_0_ms, fixDiagnosticsMS);

            logger.LogTrace(Resources.Analysis_complete_in_0ms_, analysisStopwatch.ElapsedMilliseconds);

            return solution;
        }

        private async Task<ImmutableDictionary<ProjectId, ImmutableHashSet<string>>> GetProjectDiagnosticsAsync(
            Solution solution,
            ImmutableDictionary<Project, ImmutableArray<DiagnosticAnalyzer>> projectAnalyzers,
            ImmutableHashSet<string> formattablePaths,
            FormatOptions options,
            DiagnosticSeverity severity,
            bool includeCompilerDiagnostics,
            ILogger logger,
            List<FormattedFile> formattedFiles,
            CancellationToken cancellationToken)
        {
            var result = new CodeAnalysisResult();
            foreach (var project in solution.Projects)
            {
                var analyzers = projectAnalyzers[project];
                if (analyzers.IsEmpty)
                {
                    continue;
                }

                // Run all the filtered analyzers to determine which are reporting diagnostic.
                await _runner.RunCodeAnalysisAsync(result, analyzers, project, formattablePaths, severity, includeCompilerDiagnostics, logger, cancellationToken).ConfigureAwait(false);
            }

            LogDiagnosticLocations(solution, result.Diagnostics.SelectMany(kvp => kvp.Value), options.WorkspaceFilePath, options.ChangesAreErrors, logger, formattedFiles);

            return result.Diagnostics.ToImmutableDictionary(kvp => kvp.Key.Id, kvp => kvp.Value.Select(diagnostic => diagnostic.Id).ToImmutableHashSet());

            static void LogDiagnosticLocations(Solution solution, IEnumerable<Diagnostic> diagnostics, string workspacePath, bool changesAreErrors, ILogger logger, List<FormattedFile> formattedFiles)
            {
                var workspaceFolder = Path.GetDirectoryName(workspacePath) ?? workspacePath;

                foreach (var diagnostic in diagnostics)
                {
                    var message = $"{diagnostic.GetMessage()} ({diagnostic.Id})";
                    var document = solution.GetDocument(diagnostic.Location.SourceTree);
                    if (document is null)
                    {
                        continue;
                    }

                    var filePath = document.FilePath ?? document.Name;

                    var mappedLineSpan = diagnostic.Location.GetMappedLineSpan();
                    var changePosition = mappedLineSpan.StartLinePosition;

                    var formatMessage = $"{Path.GetRelativePath(workspaceFolder, filePath)}({changePosition.Line + 1},{changePosition.Character + 1}): {message}";
                    formattedFiles.Add(new FormattedFile(document!, new[] { new FileChange(changePosition, message) }));

                    if (changesAreErrors)
                    {
                        logger.LogError(formatMessage);
                    }
                    else
                    {
                        logger.LogWarning(formatMessage);
                    }
                }
            }
        }

        private async Task<Solution> FixDiagnosticsAsync(
            Solution solution,
            ImmutableArray<DiagnosticAnalyzer> allAnalyzers,
            ImmutableArray<CodeFixProvider> allCodefixes,
            ImmutableDictionary<ProjectId, ImmutableHashSet<string>> projectDiagnostics,
            ImmutableHashSet<string> formattablePaths,
            DiagnosticSeverity severity,
            bool includeCompilerDiagnostics,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            // Determine the reported diagnostic ids
            var reportedDiagnostics = projectDiagnostics.SelectMany(kvp => kvp.Value).Distinct().ToImmutableArray();
            if (reportedDiagnostics.IsEmpty)
            {
                return solution;
            }

            // Build maps between diagnostic id and the associated analyzers and codefixes
            var analyzersByIdAndLanguage = CreateAnalyzerMap(reportedDiagnostics, allAnalyzers);
            var fixersById = CreateFixerMap(reportedDiagnostics, allCodefixes);

            // We need to run each codefix iteratively so ensure that all diagnostics are found and fixed.
            foreach (var diagnosticId in reportedDiagnostics)
            {
                var analyzersByLanguage = analyzersByIdAndLanguage[diagnosticId];
                var codefixes = fixersById[diagnosticId];

                // If there is no codefix, there is no reason to run analysis again.
                if (codefixes.IsEmpty)
                {
                    logger.LogWarning(Resources.Unable_to_fix_0_No_associated_code_fix_found, diagnosticId);
                    continue;
                }

                var result = new CodeAnalysisResult();
                foreach (var project in solution.Projects)
                {
                    // Only run analysis on projects that had previously reported the diagnostic
                    if (!projectDiagnostics.TryGetValue(project.Id, out var diagnosticIds))
                    {
                        continue;
                    }

                    var analyzers = analyzersByLanguage[project.Language];
                    await _runner.RunCodeAnalysisAsync(result, analyzers, project, formattablePaths, severity, includeCompilerDiagnostics, logger, cancellationToken).ConfigureAwait(false);
                }

                var hasDiagnostics = result.Diagnostics.Any(kvp => kvp.Value.Count > 0);
                if (hasDiagnostics)
                {
                    foreach (var codefix in codefixes)
                    {
                        var changedSolution = await _applier.ApplyCodeFixesAsync(solution, result, codefix, diagnosticId, logger, cancellationToken).ConfigureAwait(false);
                        if (changedSolution.GetChanges(solution).Any())
                        {
                            solution = changedSolution;
                        }
                    }
                }
            }

            return solution;

            static ImmutableDictionary<string, ImmutableDictionary<string, ImmutableArray<DiagnosticAnalyzer>>> CreateAnalyzerMap(
                ImmutableArray<string> diagnosticIds,
                ImmutableArray<DiagnosticAnalyzer> analyzers)
            {
                return diagnosticIds.ToImmutableDictionary(
                    id => id,
                    id => s_supportedLanguages.ToImmutableDictionary(
                        language => language,
                        language => analyzers
                            .Where(analyzer => DoesAnalyzerSupportLanguage(analyzer, language))
                            .Where(analyzer => analyzer.SupportedDiagnostics.Any(diagnostic => diagnostic.Id == id))
                            .ToImmutableArray()));
            }

            static ImmutableDictionary<string, ImmutableArray<CodeFixProvider>> CreateFixerMap(
                ImmutableArray<string> diagnosticIds,
                ImmutableArray<CodeFixProvider> fixers)
            {
                return diagnosticIds.ToImmutableDictionary(
                    id => id,
                    id => fixers
                        .Where(fixer => fixer.FixableDiagnosticIds.Contains(id))
                        .ToImmutableArray());
            }
        }

        internal static async Task<ImmutableDictionary<Project, ImmutableArray<DiagnosticAnalyzer>>> FilterBySeverityAsync(
            IEnumerable<Project> projects,
            ImmutableArray<DiagnosticAnalyzer> allAnalyzers,
            ImmutableHashSet<string> formattablePaths,
            DiagnosticSeverity minimumSeverity,
            CancellationToken cancellationToken)
        {
            // We only want to run analyzers for each project that have the potential for reporting a diagnostic with
            // a severity equal to or greater than specified.
            var projectAnalyzers = ImmutableDictionary.CreateBuilder<Project, ImmutableArray<DiagnosticAnalyzer>>();
            foreach (var project in projects)
            {
                var analyzers = ImmutableArray.CreateBuilder<DiagnosticAnalyzer>();

                // Filter analyzers by project's language
                var filteredAnalyzer = allAnalyzers.Where(analyzer => DoesAnalyzerSupportLanguage(analyzer, project.Language));
                foreach (var analyzer in filteredAnalyzer)
                {
                    // Always run naming style analyzers because we cannot determine potential severity.
                    // The reported diagnostics will be filtered by severity when they are run.
                    if (analyzer.GetType().FullName?.EndsWith("NamingStyleDiagnosticAnalyzer") == true)
                    {
                        analyzers.Add(analyzer);
                        continue;
                    }

                    var severity = await analyzer.GetSeverityAsync(project, formattablePaths, cancellationToken).ConfigureAwait(false);
                    if (severity >= minimumSeverity)
                    {
                        analyzers.Add(analyzer);
                    }
                }

                projectAnalyzers.Add(project, analyzers.ToImmutableArray());
            }

            return projectAnalyzers.ToImmutableDictionary();
        }

        private static bool DoesAnalyzerSupportLanguage(DiagnosticAnalyzer analyzer, string language)
        {
            return analyzer.GetType()
                .GetCustomAttributes(typeof(DiagnosticAnalyzerAttribute), true)
                .OfType<DiagnosticAnalyzerAttribute>()
                .Any(attribute => attribute.Languages.Contains(language));
        }
    }
}
