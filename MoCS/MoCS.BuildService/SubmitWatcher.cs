﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Timers;
using System.IO;
using System.Xml;
using MoCS.BuildService.Business;
using MoCS.Business.Objects;
using MoCS.Business.Facade;
using System.Configuration;
using MoCS.BuildService.Business.Interfaces;
using System.Threading;
using System.Collections;

namespace MoCS.BuildService
{

    public class SubmitWatcher
    {
        private Hashtable _runningSubmitsHT;
        private System.Timers.Timer _timer;
        private SystemSettings _systemSettings;
        private IFileSystem _fileSystem;

        private delegate void DoWorkNeedsTimeOutDelegate(ValidationProcess submit);

        public SubmitWatcher()
        {
            //create a synchronized wrapper around the hashtable
            Hashtable ht2 = new Hashtable();
            _runningSubmitsHT = Hashtable.Synchronized(ht2);
            _systemSettings = SettingsFactory.CreateSystemSettings();
            _fileSystem = new MoCS.BuildService.Business.FileSystemWrapper();
        }

        public void StartWatching()
        {
            ClientFacade facade = new ClientFacade();
            List<Submit> submits = facade.GetUnprocessedSubmits();

            //start watching new submits
            StartNewSubmits(submits);

            //set the timer to start periodically watching
            _timer = new System.Timers.Timer();
            _timer.Interval = GetPollingInterval();
            _timer.Elapsed += new ElapsedEventHandler(_timer_Elapsed);
            _timer.Start();

        }

        private int GetPollingInterval()
        {
            string pollingIntervalValue = ConfigurationManager.AppSettings["PollingInterval"];
            int pollingInterval = Convert.ToInt32(pollingIntervalValue);
            return pollingInterval;

        }

        private void _timer_Elapsed(object sender, ElapsedEventArgs e)
        {
            ClientFacade facade = new ClientFacade();
            List<Submit> submits = facade.GetUnprocessedSubmits();
            StartNewSubmits(submits);
            TerminateOldSubmits();
            TraceStatus();
        }

        private void StartNewSubmits(List<Submit> submits)
        {
            int millisecondsToWait = GetTimeOut();

            //start new submits
            foreach (Submit s in submits)
            {
                if (!_runningSubmitsHT.ContainsKey(s.Id.ToString()))
                {
                    //create a new thread
                    ParameterizedThreadStart threadStart = new ParameterizedThreadStart(this.BuildSolution);
                    Thread t = new Thread(threadStart);

                    ValidationProcess process = new ValidationProcess(s, DateTime.Now);
                    process.SetThread(t);

                    _runningSubmitsHT.Add(s.Id.ToString(), process);
                    t.Start(process);
                }
            }
        }

        /// <summary>
        /// get the timeout from the config
        /// </summary>
        /// <returns></returns>
        private int GetTimeOut()
        {
            int millisecondsToWait = int.Parse(ConfigurationManager.AppSettings["ProcessingTimeOut"]);
            return millisecondsToWait;
        }


        private void TerminateOldSubmits()
        {
            int timeOut = GetTimeOut();

            List<string> keysToDelete = new List<string>();

            //see if any thread has timed out
            foreach (string key in _runningSubmitsHT.Keys)
            {
                bool terminate = false;
                Thread t = ((ValidationProcess)_runningSubmitsHT[key]).Thread;
                ValidationProcess submit = null;
                bool isTimeOut = false;

                submit = (ValidationProcess)_runningSubmitsHT[key];

                if (submit.Result != null && submit.Result.Status != SubmitStatusCode.Unknown)
                {
                    terminate = true;
                }
                else
                {
                    TimeSpan span = DateTime.Now.Subtract(submit.ProcessingDate);

                    if (span.TotalMilliseconds > timeOut)
                    {
                        isTimeOut = true;
                        terminate = true;
                        if (submit.Result == null)
                        {
                            submit.Result = new ValidationResult();
                        }
                        submit.Result.Status = SubmitStatusCode.TestError;
                        submit.Result.Messages.Add("TimeOut - it took more than " + timeOut.ToString() + " ms");
                    }
                }

                if (terminate)
                {
                    //remind wich key to delete. this can't be done inside the enumeration
                    keysToDelete.Add(key);

                    //kill the thread 
                    t.Abort();

                    //terminate running processes
                    if (submit.Validator != null)
                    {
                        submit.Validator.Terminate();
                    }

                    if (isTimeOut)
                    {
                        //other statusses are saved in the process
                        SaveStatusToDatabase(submit.Submit, submit.Result);
                    }

                }
            }

            //remove this outside the foreach loop
            foreach (string key in keysToDelete)
            {
                //remove the submit
                if (_runningSubmitsHT.ContainsKey(key))
                {
                    _runningSubmitsHT.Remove(key);
                }
            }

        }


        public void BuildSolution(object vp)
        {
            ValidationProcess validationProcess = (ValidationProcess)vp;
            try
            {
                ProcessTeamSubmit(validationProcess, _systemSettings);
            }
            catch (ThreadAbortException)
            {
                Submit submit = validationProcess.Submit;
                Log("Timeout for " + submit.Team.Name + " on " + submit.TournamentAssignment.Assignment.Name);
            }
            catch (Exception ex)
            {
                Submit submit = validationProcess.Submit;
                Log(string.Format("ERROR DURING BUILD FOR: {0}-{1}:{2}: ", submit.Team.Name, submit.TournamentAssignment.Assignment.Name, ex.Message + " " + ex.GetType().ToString()));
            }
        }


        private void TraceStatus()
        {
            Console.WriteLine(DateTime.Now.ToLongTimeString() + "  submits: " + _runningSubmitsHT.Count.ToString());
        }

        private static void Log(string message)
        {
            Console.WriteLine(DateTime.Now.ToLongTimeString() + " " + message);
        }

        private static string CreateTeamDirectory(SystemSettings sysSettings, string teamName, Assignment assignment)
        {
            MoCS.BuildService.Business.FileSystemWrapper fileSystem = new Business.FileSystemWrapper();

            string resultBasePath = ConfigurationManager.AppSettings["ResultBasePath"];
            if (!resultBasePath.EndsWith(@"\"))
            {
                resultBasePath += @"\";
            }


            //prepare processing
            //create a new directory for the basepath
            fileSystem.CreateDirectoryIfNotExists(resultBasePath);

            //create a directory for the assignment
            fileSystem.CreateDirectoryIfNotExists(resultBasePath + assignment.Name);

            string teamDirName = teamName + "_" + DateTime.Now.ToString("ddMMyyyy_HHmmss");
            string teamSubmitDirName = resultBasePath + assignment.Name + @"\" + teamDirName;
            //create a new directory for the teamsubmit
            fileSystem.CreateDirectory(teamSubmitDirName);

            return teamSubmitDirName;
        }

        private static void CopyFiles(Assignment assignment, Submit submit, string teamSubmitDirName, SystemSettings systemSettings)
        {


            MoCS.BuildService.Business.FileSystemWrapper fileSystem = new Business.FileSystemWrapper();
            
            // Copy nunit.framework.dll to this directory
            fileSystem.FileCopy(Path.Combine(systemSettings.NunitAssemblyPath, "nunit.framework.dll"),
                        Path.Combine(teamSubmitDirName, "nunit.framework.dll"), true);

            //copy the file to this directory
            using (Stream target = fileSystem.FileOpenWrite(Path.Combine(teamSubmitDirName, submit.FileName)))
            {
                try
                {
                    target.Write(submit.Data, 0, submit.Data.Length);
                }
                finally
                {
                    target.Flush();
                }
            }


            // Copy the interface file
            //delete the file if it existed already
            AssignmentFile interfaceFile = assignment.AssignmentFiles.Find(af => af.Name == "InterfaceFile");

            fileSystem.DeleteFileIfExists(Path.Combine(teamSubmitDirName, interfaceFile.FileName));

            fileSystem.FileCopy(Path.Combine(assignment.Path, interfaceFile.FileName),
                        Path.Combine(teamSubmitDirName, interfaceFile.FileName));

            //copy the server testfile
            //delete the file if it existed already
            AssignmentFile serverTestFile = assignment.AssignmentFiles.Find(af => af.Name == "NunitTestFileServer");

            fileSystem.DeleteFileIfExists(Path.Combine(teamSubmitDirName, serverTestFile.FileName));

            fileSystem.FileCopy(Path.Combine(assignment.Path, serverTestFile.FileName),
                        Path.Combine(teamSubmitDirName, serverTestFile.FileName));

            //copy additional serverfiles
            List<AssignmentFile> serverFilesToCopy = assignment.AssignmentFiles.FindAll(af => af.Name == "ServerFileToCopy");
            foreach (AssignmentFile serverFileToCopy in serverFilesToCopy)
            {

                fileSystem.DeleteFileIfExists(Path.Combine(teamSubmitDirName, serverFileToCopy.FileName));

                fileSystem.FileCopy(Path.Combine(assignment.Path, serverFileToCopy.FileName),
                            Path.Combine(teamSubmitDirName, serverFileToCopy.FileName));
            }

            //copy the client testfile
            AssignmentFile clientTestFile = assignment.AssignmentFiles.Find(af => af.Name == "NunitTestFileClient");

            //delete the file if it existed already
            fileSystem.DeleteFileIfExists(Path.Combine(teamSubmitDirName, clientTestFile.FileName));

            fileSystem.FileCopy(Path.Combine(assignment.Path, clientTestFile.FileName),
                        Path.Combine(teamSubmitDirName, clientTestFile.FileName));

        }

        private static void CleanupFiles(string teamSubmitDirName)
        {
            MoCS.BuildService.Business.FileSystemWrapper fileSystem = new Business.FileSystemWrapper();
            fileSystem.FileDelete(Path.Combine(teamSubmitDirName, "nunit.framework.dll"));
        }

        private static void ProcessTeamSubmit(ValidationProcess validationProcess, SystemSettings sysSettings)
        {
            Submit submit = validationProcess.Submit;
            string teamName = submit.Team.Name;
            string assignmentName = submit.TournamentAssignment.Assignment.Name;

            Log(string.Format("Processing teamsubmit {0} for assignment {1}", teamName, assignmentName));

            //create the processor
            SubmitValidator validator = new SubmitValidator(new MoCS.BuildService.Business.FileSystemWrapper(), new ExecuteCmd());
            validationProcess.SetProcessor(validator);

            //prepare directory and files for processing
            string teamSubmitDirName = CreateTeamDirectory(sysSettings, teamName, submit.TournamentAssignment.Assignment);
            ClientFacade facade = new ClientFacade();
            MoCS.Business.Objects.Assignment assignment = facade.GetAssignmentById(submit.TournamentAssignment.Assignment.Id, true);
            CopyFiles(assignment, submit, teamSubmitDirName, sysSettings);

            //START PROCESSING

            //settings that are read from the assignment
            AssignmentSettings assignmentSettings = SettingsFactory.CreateAssignmentSettings(assignment, assignmentName);
            //settings that are from the submitprocess/team submit
            SubmitSettings submitSettings = SettingsFactory.CreateSubmitSettings(teamName, teamSubmitDirName, assignmentName);

            //set status of submit to 'processing'
            facade.UpdateSubmitStatusDetails(submit.Id, SubmitStatus.Processing, "This submitted is currently processed.", DateTime.Now);

            ValidationResult result = validator.Process(sysSettings, assignmentSettings, submitSettings);
            validationProcess.Result = result;

            Log(result.Status + " for " + submit.Team.Name + " on " + submit.TournamentAssignment.Assignment.Name);

            //save the new status to the database
            SaveStatusToDatabase(validationProcess.Submit, result);

            // Delete nunit.framework.dll from the submit dir to keep things clean
            CleanupFiles(teamSubmitDirName);
        }



        private static void SaveStatusToDatabase(Submit submit, ValidationResult result)
        {
            string teamName = submit.Team.Name;
            string assignmentName = submit.TournamentAssignment.Assignment.Name;

            string details = "";
            foreach (string detail in result.Messages)
            {
                details += detail;
            }
            if (details.Length > 1000)
            {
                details = details.Substring(0, 999);
            }

            ClientFacade facade = new ClientFacade();

            //process result
            switch (result.Status)
            {
                case SubmitStatusCode.Unknown:
                    facade.UpdateSubmitStatusDetails(submit.Id, SubmitStatus.ErrorUnknown, details, DateTime.Now);
                    break;
                case SubmitStatusCode.CompilationError:
                    facade.UpdateSubmitStatusDetails(submit.Id, SubmitStatus.ErrorCompilation, details, DateTime.Now);
                    break;
                case SubmitStatusCode.ValidationError:
                    facade.UpdateSubmitStatusDetails(submit.Id, SubmitStatus.ErrorValidation, details, DateTime.Now);
                    break;
                case SubmitStatusCode.TestError:
                    facade.UpdateSubmitStatusDetails(submit.Id, SubmitStatus.ErrorTesting, details, DateTime.Now);
                    break;
                case SubmitStatusCode.ServerError:
                    facade.UpdateSubmitStatusDetails(submit.Id, SubmitStatus.ErrorServer, details, DateTime.Now);
                    break;
                case SubmitStatusCode.Success:
                    facade.UpdateSubmitStatusDetails(submit.Id, SubmitStatus.Success, details, DateTime.Now);
                    break;
                default:
                    break;
            }


        }









    }





}
