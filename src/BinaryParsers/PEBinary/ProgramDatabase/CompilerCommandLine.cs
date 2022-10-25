﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;

using Microsoft.CodeAnalysis.Sarif.Driver;

namespace Microsoft.CodeAnalysis.BinaryParsers.ProgramDatabase
{
    /// <summary>Processes command lines stored by compilers in PDBs.</summary>
    internal struct CompilerCommandLine
    {
        // I can't defend this design where the "once-ness" and the level of the warning
        // are part of the same value; but it's what the native compiler does :(
        //
        // See email from the compiler team attached to twcsec-tfs01 bug# 17770
        //
        // (Causes many WTFs like:
        //     cl.exe /c /W1 /wd4265 /w14265 /wo4265 C4265.cpp
        // emits no warning, but
        //     cl.exe /c /W1 /wd4265 /w14265 C4265.cpp
        // does.)

        private enum WarningState
        {
            Level1 = 1,
            Level2 = 2,
            Level3 = 3,
            Level4 = 4,
            AsError,
            Once,
            Disabled
        }

        /// <summary>
        /// The raw, unadulterated command line before processing.
        /// </summary>
        public readonly string Raw;

        /// <summary>
        /// The set of warnings explicitly disabled in this command line. Sorted.
        /// </summary>
        public readonly ImmutableArray<int> WarningsExplicitlyDisabled;

        /// <summary>
        /// The warning level (/W1, /W3, /Wall, etc.) in the range [0, 4] from this command line.
        /// </summary>
        public readonly int WarningLevel;

        /// <summary>
        /// Whether or not this command line treats warnings as errors.
        /// </summary>
        public readonly bool WarningsAsErrors;

        /// <summary>
        /// Whether or not this command line enables optimizations.
        /// </summary>
        public readonly bool OptimizationsEnabled;

        /// <summary>
        /// Whether or not this command line specifies a debug C runtime library.
        /// </summary>
        public readonly bool UsesDebugCRuntime;

        /// <summary>
        /// Whether or not this command line requests String Pooling aka Eliminate Duplicate Strings aka /GF.
        /// </summary>
        public readonly bool EliminateDuplicateStringsEnabled;

        /// <summary>
        /// Whether or not this command line requests whole program optimization (/GL).
        /// </summary>
        public readonly bool WholeProgramOptimizationEnabled;

        /// <summary>
        /// Initializes a new instance of the <see cref="CompilerCommandLine"/> struct from a raw PDB-supplied command line.
        /// </summary>
        /// <param name="commandLine">The raw command line from the PDB.</param>
        public CompilerCommandLine(string commandLine)
        {
            //
            // https://msdn.microsoft.com/en-us/library/thxezb7y.aspx
            //

            this.Raw = commandLine ?? "";
            this.WarningLevel = 0;
            this.WarningsAsErrors = false;
            this.OptimizationsEnabled = false;
            this.UsesDebugCRuntime = false;
            this.EliminateDuplicateStringsEnabled = false;
            this.WholeProgramOptimizationEnabled = false;

            var explicitWarnings = new Dictionary<int, WarningState>();
            foreach (string argument in ArgumentSplitter.CommandLineToArgvW(commandLine))
            {
                if (!CommandLineHelper.IsCommandLineOption(argument))
                {
                    continue;
                }

                switch (argument.Length)
                {
                    case 2:
                        // /w Disables all compiler warnings.
                        if (argument[1] == 'w')
                        {
                            this.WarningLevel = 0;
                        }
                        break;
                    case 3:
                        if (argument[1] == 'W')
                        {
                            char wChar = argument[2];
                            if (wChar == 'X')
                            {
                                // Treats all compiler warnings as errors.
                                this.WarningsAsErrors = true;
                            }
                            else if (wChar >= '0' && wChar <= '4')
                            {
                                // Specifies the level of warning to be generated by the compiler.
                                this.WarningLevel = wChar - '0';
                            }
                        }
                        else if (argument.EndsWith("O1") || argument.EndsWith("O2") || argument.EndsWith("Og") || argument.EndsWith("Os") || argument.EndsWith("Ot") || argument.EndsWith("Ox"))
                        {
                            // https://docs.microsoft.com/cpp/build/reference/o-options-optimize-code?view=msvc-170
                            // /O1 /O2 /Og /Os /Ot /Ox are all indicative of optimizations being enabled
                            this.OptimizationsEnabled = true;

                            if (argument.EndsWith("O1") || argument.EndsWith("O2"))
                            {
                                // https://docs.microsoft.com/cpp/build/reference/gf-eliminate-duplicate-strings?view=msvc-170#remarks
                                // "/GF is in effect when /O1 or /O2 is used.".  Basically, it is not necessary to request /GF when using /O1 or /O2.
                                this.EliminateDuplicateStringsEnabled = true;
                            }
                        }
                        else if (argument.EndsWith("Od"))
                        {
                            // /Od explicitly disables optimizations
                            this.OptimizationsEnabled = false;
                        }
                        else if (argument.EndsWith("MT") || argument.EndsWith("MD"))
                        {
                            this.UsesDebugCRuntime = false;
                        }
                        else if (argument.EndsWith("GL"))
                        {
                            this.WholeProgramOptimizationEnabled = true;
                        }
                        else if (argument.EndsWith("GF"))
                        {
                            this.EliminateDuplicateStringsEnabled = true;
                        }
                        break;
                    case 4:
                        if (argument.EndsWith("WX-"))
                        {
                            // (inverse of) Treats all compiler warnings as errors.
                            this.WarningsAsErrors = false;
                        }
                        else if (argument.EndsWith("MTd") || argument.EndsWith("MDd"))
                        {
                            this.UsesDebugCRuntime = true;
                        }
                        else if (argument.EndsWith("GL-"))
                        {
                            this.WholeProgramOptimizationEnabled = false;
                        }
                        break;
                    case 5:
                        if (argument.EndsWith("Wall"))
                        {
                            // Displays all /W4 warnings and any other warnings that are not included in /W4
                            this.WarningLevel = 4;
                        }
                        break;
                    case 7:
                        if (argument[1] != 'w')
                        {
                            break;
                        }

                        WarningState state;
                        char mode = argument[2];
                        if (mode == 'd')
                        {
                            // Disables the compiler warning that is specified
                            state = WarningState.Disabled;
                        }
                        else if (mode == 'e')
                        {
                            // Treats as an error the compiler warning that is specified
                            state = WarningState.AsError;
                        }
                        else if (mode == 'o')
                        {
                            // Reports the error only once for the compiler warning that is specified
                            state = WarningState.Once;
                        }
                        else if (mode >= '1' && mode <= '4')
                        {
                            // Specifies the level for a particular warning.
                            // e.g. /w14996 sets 4996 to level 1
                            state = (WarningState)(mode - '1' + (int)WarningState.Level1);
                        }
                        else
                        {
                            break;
                        }

                        int warningNumber;
                        if (!int.TryParse(argument.Remove(0, 3), 0, CultureInfo.InvariantCulture, out warningNumber))
                        {
                            break;
                        }

                        explicitWarnings[warningNumber] = state;
                        break;
                }
            }

            ImmutableArray<int>.Builder explicitlyDisabledBuilder = ImmutableArray.CreateBuilder<int>();
            foreach (KeyValuePair<int, WarningState> entry in explicitWarnings)
            {
                bool isEnabled;
                switch (entry.Value)
                {
                    case WarningState.AsError:
                    case WarningState.Once:
                        isEnabled = true;
                        break;
                    case WarningState.Disabled:
                        isEnabled = false;
                        break;
                    case WarningState.Level1:
                        isEnabled = this.WarningLevel >= 1;
                        break;
                    case WarningState.Level2:
                        isEnabled = this.WarningLevel >= 2;
                        break;
                    case WarningState.Level3:
                        isEnabled = this.WarningLevel >= 3;
                        break;
                    case WarningState.Level4:
                        isEnabled = this.WarningLevel >= 4;
                        break;
                    default:
                        isEnabled = true;
                        Debug.Fail("Unexpected WarningState");
                        break;
                }

                if (!isEnabled)
                {
                    explicitlyDisabledBuilder.Add(entry.Key);
                }
            }

            explicitlyDisabledBuilder.Sort();
            this.WarningsExplicitlyDisabled = explicitlyDisabledBuilder.ToImmutable();
        }
    }
}
