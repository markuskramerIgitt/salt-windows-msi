﻿using Microsoft.Deployment.WindowsInstaller;
using Microsoft.Tools.WindowsInstallerXml;
using Microsoft.Win32;
using System;
using System.IO;
using System.Text.RegularExpressions;


namespace MinionConfigurationExtension {
  public class MinionConfiguration : WixExtension {
    /* 
     *   HISTORY
     *    2016-11-15  mkr service starting and stopping requires a missing C# library/reference. Instead, shellout("sc ...")
     *    2016-11-15  mkr read the registry for NSIS
     *    2016-11-13  mkr initiated, just logs the content of c:\ 
     * 
    */


    /*
     * Remove 'lifetime' data because the MSI easily only removes 'installtime' data.
     */
    [CustomAction]
    public static ActionResult DECA_UninstallKeepConfig0(Session session) {
      session.Log("MinionConfiguration.cs:: Begin DECA_UninstallKeepConfig0");
      PurgeDir(session, @"bin");
      PurgeDir(session, @"conf");
      PurgeDir(session, @"var");
      session.Log("MinionConfiguration.cs:: End DECA_UninstallKeepConfig0");
      return ActionResult.Success;
    }
    [CustomAction]
    public static ActionResult DECA_UninstallKeepConfig1(Session session) {
      session.Log("MinionConfiguration.cs:: Begin DECA_UninstallKeepConfig1");
      PurgeDir(session, @"bin");
      PurgeDir(session, @"var");
      session.Log("MinionConfiguration.cs:: End DECA_UninstallKeepConfig1");
      return ActionResult.Success;
    }
    [CustomAction]
    public static ActionResult DECA_Upgrade(Session session) {
      session.Log("MinionConfiguration.cs:: Begin DECA_Upgrade");
      String soon_conf = @"c:\salt\bin"; //TODO use root_dir
      String root_dir = "";
      try {
        root_dir = session.CustomActionData["root_dir"];
      } catch (Exception ex) {
        just_ExceptionLog("FATAL ERROR while getting Property INSTALLFOLDER", session, ex);
      }
      session.Log("DECA_Upgrade::  root_dir = " + root_dir);
      try {
        if (Directory.Exists(soon_conf)) {
          session.Log("DECA_Upgrade:: about to delete pyc from " + soon_conf);
          // Only get files that end in *.pyc
          string[] foundfiles = Directory.GetFiles(soon_conf, "*.pcy", SearchOption.AllDirectories);
          session.Log("The number of pyc files is {0}.", foundfiles.Length);
          foreach (string foundfile in foundfiles) {
            session.Log("about to delete " + foundfile);
            File.Delete(foundfile);
          }
        } else {
          session.Log("DECA_Upgrade:: no Directory " + soon_conf);
        }
      } catch (Exception ex) {
        just_ExceptionLog(@"DECA_Upgrade tried remove pyc " + soon_conf, session, ex);
      }
      
      session.Log("MinionConfiguration.cs:: End DECA_Upgrade");
      return ActionResult.Success;
    }

    private static void PurgeDir(Session session, string aDir) {
      String soon_conf = @"c:\salt\" + aDir; //TODO use root_dir
      String root_dir = "";
      try {
        root_dir = session.CustomActionData["root_dir"];
      } catch (Exception ex) {
        just_ExceptionLog("FATAL ERROR while getting Property INSTALLFOLDER", session, ex);
      }
      session.Log("PurgeDir::  root_dir = " + root_dir);
      try {
        if (Directory.Exists(soon_conf)) {
          session.Log("PurgeDir:: about to Directory.delete " + soon_conf);
          Directory.Delete(soon_conf, true);
        } else {
          session.Log("PurgeDir:: no Directory " + soon_conf);
        }
      } catch (Exception ex) {
        just_ExceptionLog(@"PurgeDir tried to delete " + soon_conf, session, ex);
      }

      // quirk for https://github.com/markuskramerIgitt/salt-windows-msi/issues/33  Exception: Access to the path 'minion.pem' is denied . Read only!
      shellout(session, @"rmdir /s /q " + soon_conf);
    }


    [CustomAction]
    public static ActionResult IMCA_read_NSIS(Session session) {
      /*
       * This CustomAction must be called "early". 
       * 
       * More comments in wix.d/MinionMSI/Product.wxs.comments.txt
      */
      session.Log("MinionConfiguration.cs:: Begin IMCA_read_NSIS");
      if (!read_master_and_id_from_all_local_config_files(session)) return ActionResult.Failure;
      session.Log("MinionConfiguration.cs:: End IMCA_read_NSIS");
      return ActionResult.Success;
    }

		private static bool read_master_and_id_from_all_local_config_files(Session session) {
			String master_from_local_config = "";
			String id_from_local_config = "";

			String[] words = { "Existing", "Custom", "Default" };
			// Read config type from MSI property  
			string CONFIG_TYPE = session["CONFIG_TYPE"];
			bool ConfigTypeKnown = words[0] == CONFIG_TYPE || words[1] == CONFIG_TYPE || words[2] == CONFIG_TYPE;

			session.Log("...MSI property  CONFIG_TYPE  =" + CONFIG_TYPE);
			session.Log("...............of known value =" + ConfigTypeKnown.ToString());
			if (! ConfigTypeKnown) {
				CONFIG_TYPE = "Existing";
				session.Log(".....therefore  CONFIG_TYPE  =" + CONFIG_TYPE);
			}


			// https://docs.saltstack.com/en/latest/topics/installation/windows.html#silent-installer-options

			if (CONFIG_TYPE == "Default") {
				master_from_local_config = "salt";
				id_from_local_config = Environment.MachineName;
			}

			if (CONFIG_TYPE == "Existing") {
				// Read master and id from MINION_CONFIGFILE  
				string MINION_CONFIGFILE = session["MINION_CONFIGFILE"];
				read_master_and_id_from_file(session, MINION_CONFIGFILE, ref master_from_local_config, ref id_from_local_config);

				// Read master and id from all *.conf files in minion.d directory, if it exists.
				// ASSUMPTION minion and minion.d are in the same folder.
				string MINION_CONFIGDIR = MINION_CONFIGFILE + ".d";
				if (Directory.Exists(MINION_CONFIGDIR)) {
					var conf_files = System.IO.Directory.GetFiles(MINION_CONFIGDIR, "*.conf");
					foreach (var conf_file in conf_files) {
						// skip _schedule.conf
						if (conf_file.EndsWith("_schedule.conf")) { continue; }
						read_master_and_id_from_file(session, conf_file, ref master_from_local_config, ref id_from_local_config);
					}
				}
			}
			// Compare master and id with MSI properties
			session.Log("...MSI property  id    =" + session["MINION_HOSTNAME"]);
			session.Log("...kept config   id    =" + id_from_local_config);
			session.Log("...MSI property  master=" + session["MASTER_HOSTNAME"]);
			session.Log("...kept config   master=" + master_from_local_config);



			if (session["MASTER_HOSTNAME"] == "#") {
				// The msi has no master_hostname  (# is our convention for "unset")
				// Let's use the kept config, or "salt"
				if (master_from_local_config != "") {
					session.Log("...using kept config master because msi property unset");
					session["MASTER_HOSTNAME"] = master_from_local_config;
				} else {
					session.Log("...neither msi property nor kept config for master. Using default MASTER_HOSTNAME");
					session["MASTER_HOSTNAME"] = "salt";
				}
			} else {
				// msi propery master wins (without any action) over kept config master
				// Just for clarity of the log
				session.Log("...msi property master changes kept config ");
			}

			var master_public_key_path = @"C:\salt\conf\pki\minion";  // TODO more flexible
			var master_public_key_filename = master_public_key_path + "\\" + @"minion_master.pub"; 
			Directory.CreateDirectory(master_public_key_path);  // TODO Security
			session.Log("...kept config master key exists " + File.Exists(master_public_key_filename));
			bool MASTER_KEY_set = session["MASTER_KEY"] != "#";
			session.Log("...msi property master key given, will (over)write file " + MASTER_KEY_set);
			if (MASTER_KEY_set) {
				String master_key_one_line = session["MASTER_KEY"];
				String master_key_many_lines = "";
				int countup = 0;
				foreach (char character in master_key_one_line) {
					master_key_many_lines += character;
					countup += 1;
					if (countup % 64 == 0) {
						master_key_many_lines += System.Environment.NewLine; 
					}
				}
				string new_master_pub_key =
					"-----BEGIN PUBLIC KEY-----" + System.Environment.NewLine +
					master_key_many_lines + System.Environment.NewLine +
					"-----END PUBLIC KEY-----";
				File.WriteAllText(master_public_key_filename, new_master_pub_key);  // TODO try..catch
			}


			return true;
		}



		// Leaves the Config
		[CustomAction]
    public static ActionResult DECA_del_NSIS(Session session)  {
      session.Log("MinionConfiguration.cs:: Begin DECA_del_NSIS");
      if (!delete_NSIS(session)) return ActionResult.Failure;
      session.Log("MinionConfiguration.cs:: End DECA_del_NSIS");
      return ActionResult.Success;
    }

    private static bool delete_NSIS(Session session) {
      /*
       * If NSIS is installed:
       *   remove salt-minion service, 
       *   remove registry
       *   remove files, except /salt/conf and /salt/var
       *   
       *   all fixed path are OK here.
       *   The msi is never peeled.
      */
      session.Log("MinionConfiguration.cs:: Begin delete_NSIS_files");
      session.Log("Environment.Version = " + Environment.Version);
      if (IntPtr.Size == 8) {
        session.Log("probably 64 bit process");
      } else {
        session.Log("probably 32 bit process");
      }
      RegistryKey reg = Registry.LocalMachine;
      // (Only?) in regedit this is under    SOFTWARE\WoW6432Node
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
        shellout(session, "sc stop salt-minion");
        session.Log("delete_NSIS_files:: Going to delete service salt-minion ...");
        shellout(session, "sc delete salt-minion");

        session.Log("delete_NSIS_files:: Going to delete ARP registry64 entry for salt-minion ...");
        try { reg.DeleteSubKeyTree(Salt_uninstall_regpath64); } catch (Exception ex) { just_ExceptionLog("", session, ex); }
        session.Log("delete_NSIS_files:: Going to delete ARP registry32 entry for salt-minion ...");
        try { reg.DeleteSubKeyTree(Salt_uninstall_regpath32); } catch (Exception ex) { just_ExceptionLog("", session, ex); }

        session.Log("delete_NSIS_files:: Going to delete files ...");
        try { Directory.Delete(@"c:\salt\bin", true); }  catch (Exception ex) {just_ExceptionLog("", session, ex);}
        try { File.Delete(@"c:\salt\uninst.exe"); } catch (Exception ex) { just_ExceptionLog("", session, ex); }
        try { File.Delete(@"c:\salt\nssm.exe"); } catch (Exception ex) { just_ExceptionLog("", session, ex); }
        try { foreach (FileInfo fi in new DirectoryInfo(@"c:\salt").GetFiles("salt*.*")) { fi.Delete(); } } catch (Exception) { ;}
      }
      session.Log("MinionConfiguration.cs:: End delete_NSIS_files");
      return true;
    }


    private static void read_master_and_id_from_file(Session session, String configfile, ref String master2, ref String id2) {
			session.Log("...kept config file " + configfile);
      bool configExists = File.Exists(configfile);
      session.Log("......exists " + configExists);
      if (!configExists) { return; }
      session.Message(InstallMessage.Progress, new Record(2, 1));  // Who is reading this?
      string[] configLines = File.ReadAllLines(configfile);
      try {
        Regex r = new Regex(@"^([a-zA-Z_]+):\s*([0-9a-zA-Z_.-]+)\s*$");
        foreach (string line in configLines) {
          if (r.IsMatch(line)) {
            Match m = r.Match(line);
            string key = m.Groups[1].ToString();
            string value = m.Groups[2].ToString();
            //session.Log("...ANY KEY " + key + " " + value);
            if (key == "master") {
							master2 = value;
							session.Log("......master " + master2);
						}
            if (key == "id") {
							id2 = value;
							session.Log("......id " + id2);
						}
          }
        }
      } catch (Exception ex) { 
         just_ExceptionLog("Looping Regexp", session, ex); 
      }
      session.Message(InstallMessage.Progress, new Record(2, 1));
			return;
    }


    // Must have this signature or cannot uninstall not even write to the log
    [CustomAction]
    public static ActionResult DECA_WriteConfig(Session session) /***/ {
      string rootDir;
      try {
        rootDir = session.CustomActionData["root_dir"];
      } catch (Exception ex) { just_ExceptionLog("Getting CustomActionData " + "root_dir", session, ex); return ActionResult.Failure; }

      session.Log(@"looking for NSIS configuration in c:\salt");
      re_use_NSIS_config_folder(session, @"c:\salt\", rootDir); // This is intentionally using the fixed NSIS installation path

      ActionResult result = ActionResult.Failure;
      result = save_CustomActionDataKeyValue_to_config_file(session, "root_dir");

      if (result == ActionResult.Success)
        result = save_CustomActionDataKeyValue_to_config_file(session, "master");

      if (result == ActionResult.Success)
        result = save_CustomActionDataKeyValue_to_config_file(session, "id");

      return result;
    }

    private static ActionResult save_CustomActionDataKeyValue_to_config_file(Session session, string SaltKey) {
      session.Message(InstallMessage.ActionStart, new Record("SetConfigKeyValue1 " + SaltKey, "SetConfigKeyValue2 " + SaltKey, "[1]"));
      session.Message(InstallMessage.Progress, new Record(0, 5, 0, 0));
      session.Log("save_CustomActionDataKeyValue_to_config_file " + SaltKey);
      string CustomActionData_value;
      try {
        CustomActionData_value = session.CustomActionData[SaltKey];
      } catch (Exception ex) { just_ExceptionLog("Getting CustomActionData " + SaltKey, session, ex); return ActionResult.Failure; }
      session.Message(InstallMessage.Progress, new Record(2, 1));
      // pattern description
      // ^        start of line
      // #*       a comment hashmark would be removed, if present
      //          anything after the colon is ignored and would be removed 
      string pattern = "^#*" + SaltKey + ":";
      string replacement = String.Format(SaltKey + ": {0}", CustomActionData_value);
      ActionResult result = replace_pattern_in_config_file(session, pattern, replacement);
      session.Message(InstallMessage.Progress, new Record(2, 1));
      session.Log("save_CustomActionDataKeyValue_to_config_file End");
      return result;
    }

    private static ActionResult replace_pattern_in_config_file(Session session, string pattern, string replacement) {
      /*
       * the pattern finds assigment and assigment commented out:
       *    #a:b
       *    a:b  
       * Only replace the first match, blank out all others
       */
      string MINION_CONFIGFILE = getConfigFileLocation(session);
      string[] configLines = File.ReadAllLines(MINION_CONFIGFILE);
      session.Message(InstallMessage.Progress, new Record(2, 1));
      session.Log("replace_pattern_in_config_file..config file    {0}", MINION_CONFIGFILE);
      session.Message(InstallMessage.Progress, new Record(2, 1));
      try {
        bool never_found_the_pattern = true;
        for (int i = 0; i < configLines.Length; i++) {
          if (Regex.IsMatch(configLines[i], pattern)) {
            if (never_found_the_pattern) {
              never_found_the_pattern = false;
              session.Log("replace_pattern_in_config_file..pattern        {0}", pattern);
              session.Log("replace_pattern_in_config_file..matched  line  {0}", configLines[i]);
              session.Log("replace_pattern_in_config_file..replaced line  {0}", replacement);
              configLines[i] = replacement + "\n";
            } else {
              configLines[i] = "\n";  // only assign the the config variable once
            }
          }
        }
      } catch (Exception ex) { just_ExceptionLog("Looping Regexp", session, ex); return ActionResult.Failure; }
      session.Message(InstallMessage.Progress, new Record(2, 1));
      try {
        File.WriteAllLines(MINION_CONFIGFILE, configLines);
      } catch (Exception ex) { just_ExceptionLog("Writing to file", session, ex); return ActionResult.Failure; }
      return ActionResult.Success;
    }


    private static void shellout(Session session, string s) {
      // This is a handmade shellout routine
      session.Log("shellout about to try " + s);
      try {
        System.Diagnostics.Process process = new System.Diagnostics.Process();
        System.Diagnostics.ProcessStartInfo startInfo = new System.Diagnostics.ProcessStartInfo();
        startInfo.WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden;
        startInfo.FileName = "cmd.exe";
        startInfo.Arguments = "/C " + s;
        process.StartInfo = startInfo;
        process.Start();
        process.WaitForExit();
      } catch (Exception ex) {
        just_ExceptionLog("shellout tried " + s, session, ex);
      }
    }

    private static void re_use_NSIS_config_folder(Session session, string old_install_path, string new_install_path) {
      session.Log("re_use_NSIS_config_folder BEGIN");
      session.Log(old_install_path  + " to " + new_install_path );
      if (old_install_path.Equals(new_install_path, StringComparison.InvariantCultureIgnoreCase)) {
        // same location!
        session.Log(old_install_path + " == " + new_install_path);
        return;
      }
      log_config_folder_content(session, old_install_path);
      if (! (File.Exists(minion_pem(old_install_path))
        && File.Exists(minion_pup(old_install_path))
        && File.Exists(master_pup(old_install_path)))) {
        session.Log("There is no complete configuration at " + old_install_path);
        session.Log("re_use_NSIS_config_folder END PREMATURLY");
        return;
      }

      // Now we assume:
      //   there is a NSIS configuration.
      //   this is not an msi upgrade but a first install
      // Therefore move configuation into the target install dir


      log_config_folder_content(session, new_install_path);
      if (File.Exists(minion_pem(new_install_path))
        || File.Exists(minion_pup(new_install_path)) 
        || File.Exists(master_pup(new_install_path))) {
        session.Log("There is a configuration at " + new_install_path);
        session.Log("No move");
        session.Log("re_use_NSIS_config_folder END PREMATURLY");
        return;
      }
      session.Log(old_install_path + "conf" + " now moving to " + new_install_path + "conf");
      // minion.pem permission do not allow to move it
      // change permission????????????
      session.Log("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!! move !!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!");
      File.Move(minion_pem(old_install_path), minion_pem(new_install_path));
      File.Move(minion_pup(old_install_path), minion_pup(new_install_path));
      File.Move(master_pup(old_install_path), master_pup(new_install_path));

      if ( Directory.Exists(minion_d_folder(old_install_path))
      && ! Directory.Exists(minion_d_folder(new_install_path))) 
        Directory.Move(minion_d_folder(old_install_path), minion_d_folder(new_install_path));

      session.Log("re_use_NSIS_config_folder END");
    }

    private static string minion_config_file(string config_folder) { return config_folder + @"conf\minion"; }
    private static string minion_d_folder(string config_folder) { return config_folder + @"conf\minion.d"; }
    private static string pki_minion_folder(string config_folder) { return config_folder + @"conf\pki\minion"; }
    private static string minion_pem(string config_folder) { return config_folder + @"conf\pki\minion\minion.pem"; }
    private static string minion_pup(string config_folder) { return config_folder + @"conf\pki\minion\minion.pub"; }
    private static string master_pup(string config_folder) { return config_folder + @"conf\pki\minion\minion_master.pub"; }


    private static bool log_config_folder_content(Session session, string potential_config_folder) {
      session.Log("potential_config_folder         = " + potential_config_folder);
      session.Log("potential_config_folder_exists  = " + Directory.Exists(potential_config_folder));
      if (!Directory.Exists(potential_config_folder)) { 
        return false;
      }

      session.Log("salt_minion_config_file        = " + minion_config_file(potential_config_folder));
      session.Log("salt_minion_config_file_exists = " + File.Exists(minion_config_file(potential_config_folder)));

      session.Log("minion_d_folder        = " + minion_d_folder(potential_config_folder));
      session.Log("minion_d_folder_exists = " + Directory.Exists(minion_d_folder(potential_config_folder)));

      session.Log("pki_minion_folder        = " + pki_minion_folder(potential_config_folder));
      session.Log("pki_minion_folder_exists = " + Directory.Exists(pki_minion_folder(potential_config_folder)));
      if (!Directory.Exists(pki_minion_folder(potential_config_folder))) {
        return false;
      }

      session.Log("minion_pem        = " + minion_pem(potential_config_folder));
      session.Log("minion_pem_exists = " + File.Exists(minion_pem(potential_config_folder)));

      session.Log("minion_pub        = " + minion_pup(potential_config_folder));
      session.Log("minion_pub_exists = " + File.Exists(minion_pup(potential_config_folder)));

      session.Log("minion_master_pub        = " + master_pup(potential_config_folder));
      session.Log("minion_master_pub_exists = " + File.Exists(master_pup(potential_config_folder)));
      return true;
    }
    /*
         * root_dir = INSTALLDIR  the planned installation dir.
     * 
         * I need to move the old installation dir to the new installation dir.
         * 
     * When?
     *  - After PurgeDir
     *  - After the user has given INSTALLDIR
     * 
         * How do I get the old installation dir previous_root_dir?
         * - try c:\salt
         * - read content from KEEP_CONFIG_File
         * 
         * if config at previous_root_dir then
         *   move to root_dir
         */
    private static string getConfigFileLocation(Session session) {
      session.Log("getConfigFileLocation BEGIN ");

      string rootDir;
      string salt_config_file;

      try {
        rootDir = session.CustomActionData["root_dir"];
      } catch (Exception ex) { just_ExceptionLog("FATAL ERROR while getting CustomActionData root_dir", session, ex); throw ex; }
      session.Log("INSTALLFOLDER == rootDir = " + rootDir);

      salt_config_file = rootDir + "conf\\minion";

      session.Log("getConfigFileLocation END");
      return salt_config_file;
    }


    private static void just_ExceptionLog(string description, Session session, Exception ex) {
      session.Log(description);
      session.Log("Exception: {0}", ex.Message.ToString());
      session.Log(ex.StackTrace.ToString());
    }


    private static bool False_after_ExceptionLog(string description, Session session, Exception ex) {
      just_ExceptionLog(description, session, ex);
      return false;
    }
  }
}
