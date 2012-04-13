using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Timer = System.Timers.Timer;

[assembly: AssemblyTitle("usb-autorun")]
[assembly: AssemblyDescription("")]

namespace USBAutorun
{
	class Program
	{
		static int Main()
		{
			var executables = new[] { ".cmd", ".bat", ".exe" };
			var watcher = new RemovableMediaWatcher
			{
				NewDriveDetected = drive =>
					drive.RootDirectory.GetFiles("autorun.*")
					.Where(file => executables.Any(file.Name.ToLowerInvariant().EndsWith))
					.Take(1)
					.Select(file => Tuple.Create(
						file.FullName,
						new AutorunDescription(drive.Name, drive.VolumeLabel, file.Name, File.ReadAllBytes(file.FullName))
					))
					.Where(Verify)
					.Select(x => x.Item1)
					.Each(Execute)
			};
			try
			{
				var abort = new AutoResetEvent(false);
				Console.CancelKeyPress += (_, __) => abort.Set();
				Console.WriteLine("Watching for attached drives. Press Ctrl+C exit.");
				watcher.Start();
				WaitHandle.WaitAll(new[] { abort });
				return 0;
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine("Error:");
				Console.Error.WriteLine(ex.Message);
				return 1;
			}
			finally
			{
				watcher.Stop().Dispose();
			}
		}

		static bool Verify(Tuple<string, AutorunDescription> file)
		{
			var autorunDescription = file.Item2;
			var authenticatedAutorunsFileName = Assembly.GetExecutingAssembly().Location + ".authenticated_files";
			var authenticatedAutoruns = (File.Exists(authenticatedAutorunsFileName) ? File.ReadAllLines(authenticatedAutorunsFileName) : new string[]{}).ToList();

			var auth = authenticatedAutoruns.SingleOrDefault(x => x.StartsWith(autorunDescription.Id)) ?? "";
			if (auth != "")
			{
				if (autorunDescription.SameAs(auth)) return true;

				var index = authenticatedAutoruns.IndexOf(auth);
				Console.WriteLine(@"
!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!
!! WARNING: AUTORUN FILE IDENTIFICATION HAS CHANGED! !!
!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!
IT IS POSSIBLE THAT SOMEONE IS DOING SOMETHING NASTY!
Someone could be trying to trick you into executing an
unknown program/script!
It is also possible that the program/script has just
been changed intentionally. The id of the file is:
{0}

Please inspect the contents of:
{1}.
Offending id in:
{1}:{2}

Are you sure you want to update and continue executing",
				autorunDescription, authenticatedAutorunsFileName, index + 1);
				authenticatedAutoruns[index] = autorunDescription.ToString();
			}
			else
			{
				Console.WriteLine();
				Console.WriteLine(@"The authenticity of '{0} ({1})\{2}' ({3}) can't be established.", autorunDescription.VolumeLabel, autorunDescription.VolumeName, autorunDescription.FileName, autorunDescription.HashShort);
				Console.Write("Are you sure you want to add and continue executing");
				authenticatedAutoruns.Add(autorunDescription.ToString());
			}
			Console.Write("(yes/no)? ");
			if (Console.ReadLine().ToLower() != "yes") return false;

			File.WriteAllLines(authenticatedAutorunsFileName, authenticatedAutoruns);
			Console.WriteLine("'{0}' was added as authenticated.", autorunDescription.Id);
			return true;
		}

		static void Execute(string fileName)
		{
			try
			{
				using (Process.Start(fileName)) { };
				Console.WriteLine();
				Console.WriteLine("[{0}] '{1}' was executed", DateTime.Now, fileName);
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine("Error executing '{0}':", fileName);
				Console.Error.WriteLine(ex.Message);
			}
		}
	}

	public class AutorunDescription
	{
		public string VolumeSerialNumber { get; private set; }
		public string VolumeName { get; private set; }
		public string VolumeLabel { get; private set; }
		public string FileName { get; private set; }
		public string HashFull { get; private set; }
		public string HashShort { get; private set; }
		public string Id { get { return string.Format("{0}@{1}", FileName, VolumeLabel); } }

		public AutorunDescription(string volumeName, string volumeLabel, string fileName, byte[] contentBytes)
		{
			var volumeSerialNumberBytes = BitConverter.GetBytes(Kernel.GetSerialNumberOfDrive(volumeName));
			var volumeNameBytes = Encoding.UTF8.GetBytes(volumeName);
			var fileNameBytes = Encoding.UTF8.GetBytes(fileName);
			VolumeSerialNumber = BitConverter.ToString(volumeSerialNumberBytes).Replace("-", "").ToLower();
			VolumeName = volumeName.TrimEnd('\\');
			VolumeLabel = volumeLabel;
			FileName = fileName;
			var salt = volumeSerialNumberBytes.Concat(volumeNameBytes).Concat(fileNameBytes).ToArray();
			var hash = new HMACSHA1(salt).ComputeHash(contentBytes);
			HashFull = BitConverter.ToString(hash).Replace("-", "").ToLower();
			HashShort = HashFull.Substring(0, 10);
		}

		public override string ToString()
		{
			return string.Format("{0}:{1}", Id, HashFull);
		}

		public bool SameAs(string other)
		{
			return other == ToString();
		}
	}

	public static class Extensions
	{
		public static IEnumerable<T> Each<T>(this IEnumerable<T> source, Action<T> visit)
		{
			foreach (var element in source) { visit(element); }
			return source;
		}
	}

	public class RemovableMediaWatcher : IDisposable
	{
		public Action<DriveInfo> NewDriveDetected = _ => { };

		public RemovableMediaWatcher()
		{
			drivesLastSeen = ReadyDrives.Select(drive => drive.Name).ToArray();
			timer = new Timer { Interval = 1000, AutoReset = false };
			timer.Elapsed += (_, __) =>
			{
				var drives = ReadyDrives;
				drives
					.Where(drive => !drivesLastSeen.Contains(drive.Name))
					.Each(NewDriveDetected);
				drivesLastSeen = drives.Select(drive => drive.Name).ToArray();
				timer.Start();
			};
			timer.Start();
		}

		private static DriveInfo[] ReadyDrives { get { return DriveInfo.GetDrives().Where(drive => drive.IsReady).ToArray(); } }

		public RemovableMediaWatcher Start()
		{
			timer.Start();
			return this;
		}

		public RemovableMediaWatcher Stop()
		{
			timer.Stop();
			return this;
		}

		public bool IsActive { get { return timer.Enabled; } }

		public void Dispose()
		{
			timer.Dispose();
		}

		private readonly Timer timer;
		private IEnumerable<string> drivesLastSeen;
	}

	public static class Kernel
	{
		//http://www.pinvoke.net/default.aspx/kernel32/GetVolumeInformation.html
		public static uint GetSerialNumberOfDrive(string driveLetter)
		{
			var volname = new StringBuilder(261);
			var fsname = new StringBuilder(261);
			uint sernum, maxlen;
			FileSystemFeature flags;
			if (!GetVolumeInformation("c:\\", volname, volname.Capacity, out sernum, out maxlen, out flags, fsname, fsname.Capacity))
				Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
			return sernum;
		}

		[DllImport("Kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
		private extern static bool GetVolumeInformation(
		  string RootPathName,
		  StringBuilder VolumeNameBuffer,
		  int VolumeNameSize,
		  out uint VolumeSerialNumber,
		  out uint MaximumComponentLength,
		  out FileSystemFeature FileSystemFlags,
		  StringBuilder FileSystemNameBuffer,
		  int nFileSystemNameSize);

		[Flags]
		private enum FileSystemFeature : uint
		{
			/// <summary>
			/// The file system supports case-sensitive file names.
			/// </summary>
			CaseSensitiveSearch = 1,
			/// <summary>
			/// The file system preserves the case of file names when it places a name on disk.
			/// </summary>
			CasePreservedNames = 2,
			/// <summary>
			/// The file system supports Unicode in file names as they appear on disk.
			/// </summary>
			UnicodeOnDisk = 4,
			/// <summary>
			/// The file system preserves and enforces access control lists (ACL).
			/// </summary>
			PersistentACLS = 8,
			/// <summary>
			/// The file system supports file-based compression.
			/// </summary>
			FileCompression = 0x10,
			/// <summary>
			/// The file system supports disk quotas.
			/// </summary>
			VolumeQuotas = 0x20,
			/// <summary>
			/// The file system supports sparse files.
			/// </summary>
			SupportsSparseFiles = 0x40,
			/// <summary>
			/// The file system supports re-parse points.
			/// </summary>
			SupportsReparsePoints = 0x80,
			/// <summary>
			/// The specified volume is a compressed volume, for example, a DoubleSpace volume.
			/// </summary>
			VolumeIsCompressed = 0x8000,
			/// <summary>
			/// The file system supports object identifiers.
			/// </summary>
			SupportsObjectIDs = 0x10000,
			/// <summary>
			/// The file system supports the Encrypted File System (EFS).
			/// </summary>
			SupportsEncryption = 0x20000,
			/// <summary>
			/// The file system supports named streams.
			/// </summary>
			NamedStreams = 0x40000,
			/// <summary>
			/// The specified volume is read-only.
			/// </summary>
			ReadOnlyVolume = 0x80000,
			/// <summary>
			/// The volume supports a single sequential write.
			/// </summary>
			SequentialWriteOnce = 0x100000,
			/// <summary>
			/// The volume supports transactions.
			/// </summary>
			SupportsTransactions = 0x200000,
		}

	}
}
