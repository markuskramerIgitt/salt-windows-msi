﻿using Microsoft.Deployment.WindowsInstaller;
using Microsoft.Tools.WindowsInstallerXml;
using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Management;  // Reference C:\Windows\Microsoft.NET\Framework\v2.0.50727\System.Management.dll
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.RegularExpressions;



namespace MinionConfigurationExtension {
    public class MinionConfiguration : WixExtension {


        [CustomAction]
        public static ActionResult ReadConfig_IMCAC(Session session) {
            /*
             * When installatioin starts,there might be a previous installation.
             * From the previous installation, we read only two properties, that we present in the installer:
              *  - master
              *  - id
              *
              *  This function reads these two properties from
              *   - the 2 msi properties:
              *     - MASTER
              *     - MINION_ID
              *   - files from a provious installations:
              *     - the number of file the function searches depend on CONFIGURATION_TYPE
              *   - dependend on CONFIGURATION_TYPE, default values can be:
              *     - master = "salt"
              *     - id = %hostname%
              *
              *
              *  This function writes its results in the 2 msi properties:
              *   - MASTER
              *   - MINION_ID
              *
              *   A GUI installation will show these msi properties because this function is called before the GUI.
              *
              */
            session.Log("...BEGIN ReadConfig_IMCAC");
            string Manufacturer          = cutil.get_property_IMCAC(session, "Manufacturer");
            string ProductName           = cutil.get_property_IMCAC(session, "ProductName");
            string MOVE_CONF_PROGRAMDATA = cutil.get_property_IMCAC(session, "MOVE_CONF_PROGRAMDATA");
            string ProgramData           = System.Environment.GetEnvironmentVariable("ProgramData");
            string install_dir           = cutil.get_reg_SOFTWARE(session, @"Salt Project\Salt", "install_dir");
            string root_dir              = cutil.get_reg_SOFTWARE(session, @"Salt Project\Salt", "root_dir");

            string CONFIGDIROld = @"C:\" + ProductName;
            string CONFIGDIRNew =  ProgramData + @"\" + Manufacturer + @"\" + ProductName;
            session["CONFIGDIROld"] = CONFIGDIROld;
            session["CONFIGDIRNew"] = CONFIGDIRNew;

            string abortReason = "";
            if (MOVE_CONF_PROGRAMDATA == "1") {
                if (Directory.Exists(CONFIGDIROld) && Directory.Exists(CONFIGDIRNew)) {
                    abortReason = CONFIGDIROld + " and " + CONFIGDIRNew + " must not both exist when MOVE_CONF_PROGRAMDATA=1.  ";
                }
            }
            if (abortReason.Length > 0) {
                session["AbortReason"] = abortReason;
            }

            session.Log("...Searching minion config file for reading master and id");
            string main_config = cutil.get_file_that_exist(session, new string[] {
                root_dir + @"\conf\minion",
                CONFIGDIRNew + @"\conf\minion",
                CONFIGDIROld + @"\conf\minion"});
            string MINION_CONFIGDIR = "";


            if (main_config.Length > 0) {
                MINION_CONFIGDIR = main_config + ".d";
                FileSecurity fileSecurity = File.GetAccessControl(main_config);
                IdentityReference sid = fileSecurity.GetOwner(typeof(SecurityIdentifier));
                NTAccount ntAccount = sid.Translate(typeof(NTAccount)) as NTAccount;
                session.Log("...owner of the minion config file " + ntAccount.Value);

                //NO WindowsPrincipal MyPrincipal = new WindowsPrincipal(sid);
                // Groups??
            }

            // Set the default values for master and id
            String master_from_previous_installation = "";
            String id_from_previous_installation = "";
            // Read master and id from main config file (if such a file exists)
            read_master_and_id_from_file_IMCAC(session, main_config, ref master_from_previous_installation, ref id_from_previous_installation);
            // Read master and id from minion.d/*.conf (if they exist)
            if (Directory.Exists(MINION_CONFIGDIR)) {
                var conf_files = System.IO.Directory.GetFiles(MINION_CONFIGDIR, "*.conf");
                foreach (var conf_file in conf_files) {
                    if (conf_file.Equals("_schedule.conf")) { continue; }            // skip _schedule.conf
                    read_master_and_id_from_file_IMCAC(session, conf_file, ref master_from_previous_installation, ref id_from_previous_installation);
                }
            }

            session.Log("...CONFIG_TYPE msi property  = " + session["CONFIG_TYPE"]);
            session.Log("...MASTER      msi property  = " + session["MASTER"]);
            session.Log("...MINION_ID   msi property  = " + session["MINION_ID"]);

            if (session["CONFIG_TYPE"] == "Default") {
                /* Overwrite the existing config if present with the default config for salt.
                 */

                if (session["MASTER"] == "") {
                    session["MASTER"] = "salt";
                    session.Log("...MASTER set to salt because it was unset and CONFIG_TYPE=Default");
                }
                if (session["MINION_ID"] == "") {
                    session["MINION_ID"] = Environment.MachineName;
                    session.Log("...MINION_ID set to hostname because it was unset and CONFIG_TYPE=Default");
                }

                // Would be more logical in WriteConfig, but here is easier and no harm
                Backup_configuration_files_from_previous_installation(session);

            } else {
                /////////////////master
                if (session["MASTER"] == "") {
                    session.Log("...MASTER       kept config   =" + master_from_previous_installation);
                    if (master_from_previous_installation != "") {
                        session["MASTER"] = master_from_previous_installation;
                        session.Log("...MASTER set to kept config");
                    } else {
                        session["MASTER"] = "salt";
                        session.Log("...MASTER set to salt because it was unset and no kept config");
                    }
                }

                ///////////////// minion id
                if (session["MINION_ID"] == "") {
                    session.Log("...MINION_ID   kept config   =" + id_from_previous_installation);
                    if (id_from_previous_installation != "") {
                        session.Log("...MINION_ID set to kept config ");
                        session["MINION_ID"] = id_from_previous_installation;
                    } else {
                        session["MINION_ID"] = Environment.MachineName;
                        session.Log("...MINION_ID set to hostname because it was unset and no previous installation and CONFIG_TYPE!=Default");
                    }
                }
            }

            // Save the salt-master public key
            // This assumes the install will be done.
            // Saving should only occur in WriteConfig_DECAC,
            // IMCAC is easier and no harm because there is no public master key in the installer.
            string MASTER_KEY = cutil.get_property_IMCAC(session, "MASTER_KEY");
            string PKIMINIONDIR = cutil.get_property_IMCAC(session, "PKIMINIONDIR");
            var master_public_key_filename = Path.Combine(PKIMINIONDIR, "minion_master.pub");
            session.Log("...master_public_key_filename           = " + master_public_key_filename);
            bool MASTER_KEY_set = MASTER_KEY != "";
            session.Log("...master key earlier config file exists = " + File.Exists(master_public_key_filename));
            session.Log("...master key msi property given         = " + MASTER_KEY_set);
            if (MASTER_KEY_set) {
                String master_key_lines = "";   // Newline after 64 characters
                int count_characters = 0;
                foreach (char character in MASTER_KEY) {
                    master_key_lines += character;
                    count_characters += 1;
                    if (count_characters % 64 == 0) {
                        master_key_lines += Environment.NewLine;
                    }
                }
                string new_master_pub_key =
                  "-----BEGIN PUBLIC KEY-----" + Environment.NewLine +
                  master_key_lines + Environment.NewLine +
                  "-----END PUBLIC KEY-----";
                if (!Directory.Exists(session["PKIMINIONDIR"])) {
                    // The <Directory> declaration in Product.wxs does not create the folders
                    Directory.CreateDirectory(session["PKIMINIONDIR"]);
                }
                File.WriteAllText(master_public_key_filename, new_master_pub_key);
            }
            session.Log("...END ReadConfig_IMCAC");
            return ActionResult.Success;
        }


        private static void read_master_and_id_from_file_IMCAC(Session session, String configfile, ref String ref_master, ref String ref_id) {
            session.Log("...searching master and id in " + configfile);
            bool configExists = File.Exists(configfile);
            session.Log("......file exists " + configExists);
            if (!configExists) { return; }
            string[] configLines = File.ReadAllLines(configfile);
            Regex r = new Regex(@"^([a-zA-Z_]+):\s*([0-9a-zA-Z_.-]+)\s*$");
            foreach (string line in configLines) {
                if (r.IsMatch(line)) {
                    Match m = r.Match(line);
                    string key = m.Groups[1].ToString();
                    string value = m.Groups[2].ToString();
                    //session.Log("...ANY KEY " + key + " " + value);
                    if (key == "master") {
                        ref_master = value;
                        session.Log("......master " + ref_master);
                    }
                    if (key == "id") {
                        ref_id = value;
                        session.Log("......id " + ref_id);
                    }
                }
            }
        }

       [CustomAction]
        public static void stop_service(Session session, string a_service) {
            // the installer cannot assess the log file unless it is released.
            session.Log("...stop_service " + a_service);
            ServiceController service = new ServiceController(a_service);
            service.Stop();
            var timeout = new TimeSpan(0, 0, 1); // seconds
            service.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
        }

        public static void kill_python_exe(Session session) {
            // because a running process can prevent removal of files
            // Get full path and command line from running process
            using (var wmi_searcher = new ManagementObjectSearcher
                ("SELECT ProcessID, ExecutablePath, CommandLine FROM Win32_Process WHERE Name = 'python.exe'")) {
                foreach (ManagementObject wmi_obj in wmi_searcher.Get()) {
                    try {
                        String ProcessID = wmi_obj["ProcessID"].ToString();
                        Int32 pid = Int32.Parse(ProcessID);
                        String ExecutablePath = wmi_obj["ExecutablePath"].ToString();
                        String CommandLine = wmi_obj["CommandLine"].ToString();
                        if (CommandLine.ToLower().Contains("salt") || ExecutablePath.ToLower().Contains("salt")) {
                            session.Log("...kill_python_exe " + ExecutablePath + " " + CommandLine);
                            Process proc11 = Process.GetProcessById(pid);
                            proc11.Kill();
                            System.Threading.Thread.Sleep(10);
                        }
                    } catch (Exception) {
                        // ignore wmiresults without these properties
                    }
                }
            }
        }

        [CustomAction]
        public static ActionResult del_NSIS_DECAC(Session session) {
            // Leaves the Config
            /*
             * If NSIS is installed:
             *   remove salt-minion service,
             *   remove registry
             *   remove files, except /salt/conf and /salt/var
             *
             *   Instead of the above, we cannot use uninst.exe because the service would no longer start.
            */
            session.Log("...BEGIN del_NSIS_DECAC");
            RegistryKey reg = Registry.LocalMachine;
            // ?When this is under    SOFTWARE\WoW6432Node
            string Salt_uninstall_regpath64 = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Salt Minion";
            string Salt_uninstall_regpath32 = @"SOFTWARE\WoW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Salt Minion";
            var SaltRegSubkey64 = reg.OpenSubKey(Salt_uninstall_regpath64);
            var SaltRegSubkey32 = reg.OpenSubKey(Salt_uninstall_regpath32);

            bool NSIS_is_installed64 = (SaltRegSubkey64 != null) && SaltRegSubkey64.GetValue("UninstallString").ToString().Equals(@"c:\salt\uninst.exe", StringComparison.OrdinalIgnoreCase);
            bool NSIS_is_installed32 = (SaltRegSubkey32 != null) && SaltRegSubkey32.GetValue("UninstallString").ToString().Equals(@"c:\salt\uninst.exe", StringComparison.OrdinalIgnoreCase);
            session.Log("delete_NSIS_files:: NSIS_is_installed64 = " + NSIS_is_installed64);
            session.Log("delete_NSIS_files:: NSIS_is_installed32 = " + NSIS_is_installed32);
            if (NSIS_is_installed64 || NSIS_is_installed32) {
                session.Log("delete_NSIS_files:: Going to stop service salt-minion ...");
                cutil.shellout(session, "sc stop salt-minion");
                session.Log("delete_NSIS_files:: Going to delete service salt-minion ...");
                cutil.shellout(session, "sc delete salt-minion"); // shellout waits, but does sc? Does this work?

                session.Log("delete_NSIS_files:: Going to delete ARP registry64 entry for salt-minion ...");
                try { reg.DeleteSubKeyTree(Salt_uninstall_regpath64); } catch (Exception ex) { cutil.just_ExceptionLog("", session, ex); }
                session.Log("delete_NSIS_files:: Going to delete ARP registry32 entry for salt-minion ...");
                try { reg.DeleteSubKeyTree(Salt_uninstall_regpath32); } catch (Exception ex) { cutil.just_ExceptionLog("", session, ex); }

                session.Log("delete_NSIS_files:: Going to delete files ...");
                try { Directory.Delete(@"c:\salt\bin", true); } catch (Exception ex) { cutil.just_ExceptionLog("", session, ex); }
                try { File.Delete(@"c:\salt\uninst.exe"); } catch (Exception ex) { cutil.just_ExceptionLog("", session, ex); }
                try { File.Delete(@"c:\salt\nssm.exe"); } catch (Exception ex) { cutil.just_ExceptionLog("", session, ex); }
                try { foreach (FileInfo fi in new DirectoryInfo(@"c:\salt").GetFiles("salt*.*")) { fi.Delete(); } } catch (Exception) {; }
            }
            session.Log("...END del_NSIS_DECAC");
            return ActionResult.Success;
        }


        [CustomAction]
        public static ActionResult WriteConfig_DECAC(Session session) {
            /*
             * This function must leave the config files according to the CONFIG_TYPE's 1-3
             * This function is deferred (_DECAC)
             * This function runs after the msi has created the c:\salt\conf\minion file, which is a comment-only text.
             * If there was a previous install, there could be many config files.
             * The previous install c:\salt\conf\minion file could contain non-comments.
             * One of the non-comments could be master.
             * It could be that this installer has a different master.
             *
             */
            // Must have this signature or cannot uninstall not even write to the log
            session.Log("...BEGIN WriteConfig_DECAC");
            // Get msi properties
            string MOVE_CONF_PROGRAMDATA = cutil.get_property_DECAC(session, "MOVE_CONF_PROGRAMDATA");
            string INSTALLDIR = cutil.get_property_DECAC(session, "INSTALLDIR");
            string minion_config = cutil.get_property_DECAC(session, "minion_config");
            // Get environment variables
            string ProgramData = System.Environment.GetEnvironmentVariable("ProgramData");
            // Write to registry
            cutil.set_reg(session, @"SOFTWARE\Salt Project\Salt", "install_dir", INSTALLDIR);
            if (MOVE_CONF_PROGRAMDATA == "1" || minion_config.Length > 0) {
                cutil.set_reg(session, @"SOFTWARE\Salt Project\Salt", "root_dir", ProgramData + @"\" + @"Salt Project\Salt");
                cutil.set_reg(session, @"SOFTWARE\Salt Project\Salt", "REMOVE_CONFIG", "1");
            } else {
                cutil.set_reg(session, @"SOFTWARE\Salt Project\Salt", "root_dir", @"C:\Salt");
            }
            // Write, move or delete files
            if (minion_config.Length > 0) {
                apply_minion_config_DECAC(session, minion_config);
            } else {
                string master = "";
                string id = "";
                if (!replace_Saltkey_in_previous_configuration_DECAC(session, "master", ref master)) {
                    append_to_config_DECAC(session, "master", master);
                }
                if (!replace_Saltkey_in_previous_configuration_DECAC(session, "id", ref id)) {
                    append_to_config_DECAC(session, "id", id);
                }
                save_custom_config_file_if_config_type_demands_DECAC(session);
            }
            session.Log("...END WriteConfig_DECAC");
            return ActionResult.Success;
        }

        private static void save_custom_config_file_if_config_type_demands_DECAC(Session session) {
            session.Log("...save_custom_config_file_if_config_type_demands_DECAC");
            string config_type    = cutil.get_property_DECAC(session, "config_type");
            string custom_config1 = cutil.get_property_DECAC(session, "custom_config");
            string ROOTDIR        = cutil.get_property_DECAC(session, "ROOTDIR");

            string custom_config_final = "";
            if (!(config_type == "Custom" && custom_config1.Length > 0 )) {
                return;
            }
            if (File.Exists(custom_config1)) {
                session.Log("...found custom_config1 " + custom_config1);
                custom_config_final = custom_config1;
            } else {
                // try relative path
                string directory_of_the_msi = cutil.get_property_DECAC(session, "sourcedir");
                string custom_config2 = Path.Combine(directory_of_the_msi, custom_config1);
                if (File.Exists(custom_config2)) {
                    session.Log("...found custom_config2 " + custom_config2);
                    custom_config_final = custom_config2;
                } else {
                    session.Log("...no custom_config1 " + custom_config1);
                    session.Log("...no custom_config2 " + custom_config2);
                    return;
                }
            }
            Backup_configuration_files_from_previous_installation(session);
            // lay down a custom config passed via the command line
            string content_of_custom_config_file = string.Join(Environment.NewLine, File.ReadAllLines(custom_config_final));
            cutil.Write_file(session, ROOTDIR + @"\conf", "minion", content_of_custom_config_file);
        }

       [CustomAction]
        public static ActionResult DeleteConfig_DECAC(Session session) {
            // This uninstalls the current install.
            // The current install wrote to registry SOFTWARE\Salt Project\Salt
            // This uninstall relies on registry SOFTWARE\Salt Project\Salt
            session.Log("...BEGIN DeleteConfig_DECAC");
            kill_python_exe(session);

            // Determine wether to delete everything
            string REMOVE_CONFIG_prop = cutil.get_property_DECAC(session, "REMOVE_CONFIG");
            string REMOVE_CONFIG_reg = cutil.get_reg_SOFTWARE(session, @"Salt Project\Salt", "REMOVE_CONFIG");
            bool DELETE_EVERYTHING = REMOVE_CONFIG_prop == "1" || REMOVE_CONFIG_reg == "1";

            // Determine install_dir, root_dir
            string install_dir = cutil.get_reg_SOFTWARE(session, @"Salt Project\Salt", "install_dir");
            string root_dir    = cutil.get_reg_SOFTWARE(session, @"Salt Project\Salt", "root_dir");

            // Delete install_dir, root_dir, registry subkey
            cutil.del_dir(session, install_dir, "");     // msi only deletes what it installed, not *.pyc.
            if (DELETE_EVERYTHING) {
                cutil.del_dir(session, root_dir, "");
                cutil.del_reg(session, @"SOFTWARE\Salt Project\Salt");
                cutil.del_reg(session, @"SOFTWARE\WoW6432Node\Salt Project\Salt");
            } else {
                cutil.del_dir(session, root_dir, "var");
                cutil.del_dir(session, root_dir, "srv");
            }

            session.Log("...END DeleteConfig_DECAC");
            return ActionResult.Success;
        }




        private static void apply_minion_config_DECAC(Session session, string minion_config) {
            // Precondition: parameter minion_config contains the content of the MINION_CONFI property and is not empty
            // Remove all other config
            session.Log("...apply_minion_config_DECAC BEGIN");
            string CONFDIR           = cutil.get_property_DECAC(session, "CONFDIR");
            string MINION_D_DIR = cutil.get_property_DECAC(session, "MINION_D_DIR");
            // Write conf/minion
            string lines = minion_config.Replace("^", Environment.NewLine);
            cutil.Writeln_file(session, CONFDIR, "minion", lines);
            // Remove conf/minion_id
            string minion_id = Path.Combine(CONFDIR, "minion_id");
            session.Log("...searching " + minion_id);
            if (File.Exists(minion_id)) {
                File.Delete(minion_id);
                session.Log("...deleted   " + minion_id);
            }
            // Remove conf/minion.d/*.conf
            session.Log("...searching *.conf in " + MINION_D_DIR);
            if (Directory.Exists(MINION_D_DIR)) {
                var conf_files = System.IO.Directory.GetFiles(MINION_D_DIR, "*.conf");
                foreach (var conf_file in conf_files) {
                    File.Delete(conf_file);
                    session.Log("...deleted   " + conf_file);
                }
            }
            session.Log(@"...apply_minion_config_DECAC END");
        }


        private static bool replace_Saltkey_in_previous_configuration_DECAC(Session session, string SaltKey, ref string CustomActionData_value) {
            // Read SaltKey properties and convert some from 1 to True or to False
            bool replaced = false;

            session.Log("...replace_Saltkey_in_previous_configuration_DECAC Key   " + SaltKey);
            CustomActionData_value = cutil.get_property_DECAC(session, SaltKey);

            // pattern description
            // ^        start of line
            //          anything after the colon is ignored and would be removed
            string pattern = "^" + SaltKey + ":";
            string replacement = String.Format(SaltKey + ": {0}", CustomActionData_value);

            // Replace in config file
            replaced = replace_pattern_in_config_file_DECAC(session, pattern, replacement);

            session.Log(@"...replace_Saltkey_in_previous_configuration_DECAC found or replaces " + replaced.ToString());
            return replaced;
        }

        public static string getConfigFileLocation_DECAC(Session session) {
            string CONFDIR           = cutil.get_property_DECAC(session, "CONFDIR");
            return Path.Combine(CONFDIR, @"minion");
        }

        private static bool replace_pattern_in_config_file_DECAC(Session session, string pattern, string replacement) {
            /*
             * config file means: conf/minion
             */
            bool replaced_in_any_file = false;
            string MINION_CONFIGFILE = getConfigFileLocation_DECAC(session);

            replaced_in_any_file |= replace_in_file_DECAC(session, MINION_CONFIGFILE, pattern, replacement);

            return replaced_in_any_file;
        }


        static private void append_to_config_DECAC(Session session, string key, string value) {
            insert_value_after_comment_or_end_in_minionconfig_file(session, key, value);
        }


        static private void insert_value_after_comment_or_end_in_minionconfig_file(Session session, string key, string value) {
            session.Log("...insert_value_after_comment_or_end_in_minionconfig_file");
            string CONFDIR           = cutil.get_property_DECAC(session, "CONFDIR");
            string MINION_CONFIGFILE = Path.Combine(CONFDIR, "minion");
            session.Log("... MINION_CONFIGFILE {0}", MINION_CONFIGFILE);
            bool file_exists = File.Exists(MINION_CONFIGFILE);
            session.Log("...file exists {0}", file_exists);
            if (!file_exists) {
                Directory.CreateDirectory(CONFDIR);  // Any and all directories specified in path are created
                File.Create(MINION_CONFIGFILE).Close();
            }
            string[] configLines_in = File.ReadAllLines(MINION_CONFIGFILE);
            string[] configLines_out = new string[configLines_in.Length + 1];
            int configLines_out_index = 0;

            session.Log("...insert_value_after_comment_or_end  key  {0}", key);
            session.Log("...insert_value_after_comment_or_end  value  {0}", value);
            bool found = false;
            for (int i = 0; i < configLines_in.Length; i++) {
                configLines_out[configLines_out_index++] = configLines_in[i];
                if (!found && configLines_in[i].StartsWith("#" + key + ":")) {
                    found = true;
                    session.Log("...insert_value_after_comment_or_end..found the # in       {0}", configLines_in[i]);
                    configLines_out[configLines_out_index++] = key + ": " + value;
                }
            }
            if (!found) {
                session.Log("...insert_value_after_comment_or_end..end");
                configLines_out[configLines_out_index++] = key + ": " + value;
            }
            File.WriteAllLines(MINION_CONFIGFILE, configLines_out);
        }


        private static bool replace_in_file_DECAC(Session session, string config_file, string pattern, string replacement) {
            bool replaced = false;
            bool found = false;
            session.Log("...replace_in_file_DECAC   config file    {0}", config_file);
            bool file_exists = File.Exists(config_file);
            session.Log("...file exists {0}", found);
            if (!file_exists) {
                return false;
            }
            string[] configLines = File.ReadAllLines(config_file);
            session.Log("...replace_in_file_DECAC   lines          {0}", configLines.Length);

            for (int i = 0; i < configLines.Length; i++) {
                if (configLines[i].Equals(replacement)) {
                    found = true;
                    session.Log("...found the replacement in line        {0}", configLines[i]);
                }
                if (Regex.IsMatch(configLines[i], pattern)) {
                    session.Log("...matched  line  {0}", configLines[i]);
                    configLines[i] = replacement;
                    replaced = true;
                }
            }
            session.Log("...replace_in_file_DECAC   found          {0}", found);
            session.Log("...replace_in_file_DECAC   replaced       {0}", replaced);
            if (replaced) {
                File.WriteAllLines(config_file, configLines);
            }
            return replaced || found;
        }


        private static void Backup_configuration_files_from_previous_installation(Session session) {
            session.Log("...Backup_configuration_files_from_previous_installation");
            string timestamp_bak = "-" + DateTime.Now.ToString("yyyy-MM-ddTHH-mm-ss") + ".bak";
            session.Log("...timestamp_bak = " + timestamp_bak);
            cutil.Move_file(session, @"C:\salt\conf\minion", timestamp_bak);
            cutil.Move_file(session, @"C:\salt\conf\minion_id", timestamp_bak);
            cutil.Move_dir(session, @"C:\salt\conf\minion.d", timestamp_bak);
        }
    }
}
