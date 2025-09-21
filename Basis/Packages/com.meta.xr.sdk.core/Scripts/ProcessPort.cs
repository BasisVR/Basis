/*
 * Copyright (c) Meta Platforms, Inc. and affiliates.
 * All rights reserved.
 *
 * Licensed under the Oculus SDK License Agreement (the "License");
 * you may not use the Oculus SDK except in compliance with the License,
 * which is provided at the time of installation or download, or which
 * otherwise accompanies this software in either electronic or hard copy form.
 *
 * You may obtain a copy of the License at
 *
 * https://developer.oculus.com/licenses/oculussdk/
 *
 * Unless required by applicable law or agreed to in writing, the Oculus SDK
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

using UnityEngine;

public class ProcessPort
{
    public override string ToString()
    {
        return string.Format("{0}({1}) ({2} port {3})", this.processName, this.processId, this.protocol, this.portNumber);
    }

    public string processName { get; set; }
    public int processId { get; set; }
    public string portNumber { get; set; }
    public string protocol { get; set; }

    private static string LookupProcess(int pid)
    {
        string procName;
        try
        {
            procName = Process.GetProcessById(pid).ProcessName;
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogError(ex);
            procName = "-";
        }
        return procName;
    }

    public static List<ProcessPort> GetProcessesByPort(string targetPort)
    {
        var ports = new List<ProcessPort>();

        try
        {
            using (Process p = new Process())
            {
                var ps = new ProcessStartInfo();
                ps.Arguments = "-a -n -o";
                ps.FileName = "netstat.exe";
                ps.UseShellExecute = false;
                ps.WindowStyle = ProcessWindowStyle.Hidden;
                ps.RedirectStandardInput = true;
                ps.RedirectStandardOutput = true;
                ps.RedirectStandardError = true;

                p.StartInfo = ps;
                p.Start();

                StreamReader stdOutput = p.StandardOutput;
                StreamReader stdError = p.StandardError;

                string content = stdOutput.ReadToEnd() + stdError.ReadToEnd();
                int exitStatus = p.ExitCode;
                if (exitStatus != 0)
                {
                    UnityEngine.Debug.LogError("netstat call failed");
                    return ports;
                }

                Regex lineRE = new Regex("\r\n");
                Regex tokenRE = new Regex("\\s+");
                Regex localAddressRE = new Regex(@"\[(.*?)\]");

                string[] rows = lineRE.Split(content);
                foreach (string row in rows)
                {
                    string[] tokens = tokenRE.Split(row);
                    if (tokens.Length > 4 && (tokens[1].Equals("UDP") || tokens[1].Equals("TCP")))
                    {
                        string localAddress = localAddressRE.Replace(tokens[2], "1.1.1.1");
                        string portNumber = localAddress.Split(':')[1];
                        if (targetPort != portNumber)
                        {
                            continue;
                        }
                        int processId = 0;
                        try
                        {
                            processId = tokens[1].Equals("UDP") ? Convert.ToInt32(tokens[4]) : Convert.ToInt32(tokens[5]);
                        }
                        catch (Exception ex)
                        {
                            UnityEngine.Debug.LogError(tokens[1] + " " + tokens[4] + " " + tokens[5]);
                            throw ex;
                        }
                        ports.Add(new ProcessPort
                        {
                            protocol = localAddress.Contains("1.1.1.1") ? String.Format("{0}v6", tokens[1]) : String.Format("{0}v4", tokens[1]),
                            portNumber = portNumber,
                            processName = LookupProcess(processId),
                            processId = processId
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogError(ex.Message);
        }

        return ports;
    }
}
