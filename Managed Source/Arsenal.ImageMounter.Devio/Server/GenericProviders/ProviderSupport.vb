﻿
''''' ProviderSupport.vb
''''' 
''''' Copyright (c) 2012-2022, Arsenal Consulting, Inc. (d/b/a Arsenal Recon) <http://www.ArsenalRecon.com>
''''' This source code and API are available under the terms of the Affero General Public
''''' License v3.
'''''
''''' Please see LICENSE.txt for full license terms, including the availability of
''''' proprietary exceptions.
''''' Questions, comments, or requests for clarification: http://ArsenalRecon.com/contact/
'''''

Imports System.IO
Imports System.Runtime.CompilerServices
Imports System.Runtime.InteropServices
Imports System.Runtime.Versioning
Imports System.Security.Cryptography
Imports System.Threading
Imports Arsenal.ImageMounter.Devio.Extensions
Imports Arsenal.ImageMounter.Devio.Server.Interaction
Imports Arsenal.ImageMounter.Devio.Server.SpecializedProviders
Imports Arsenal.ImageMounter.Extensions
Imports Arsenal.ImageMounter.IO
Imports DiscUtils

Namespace Server.GenericProviders

    Public Module ProviderSupport

        Public Property ImageConversionIoBufferSize As Integer = 2 << 20

        Public Function GetMultiSegmentFiles(FirstFile As String) As String()

#If NETSTANDARD2_1_OR_GREATER OrElse NETCOREAPP Then

            Dim pathpart = Path.GetDirectoryName(FirstFile.AsSpan())
            Dim filepart = Path.GetFileNameWithoutExtension(FirstFile.AsSpan())
            Dim extension = Path.GetExtension(FirstFile.AsSpan())
            Dim foundfiles As String() = Nothing

            If extension.EndsWith("01", StringComparison.Ordinal) OrElse
                extension.EndsWith("00", StringComparison.Ordinal) Then

                Dim start = extension.Length - 3

                While start >= 0 AndAlso Char.IsDigit(extension.GetItem(start))
                    start -= 1
                End While

                start += 1

                Dim segmentnumberchars As New String("?"c, extension.Length - start)
                Dim namebase = String.Concat(filepart, extension.Slice(0, start))
                Dim pathbase = Path.Join(pathpart, namebase)
                Dim dir_name = pathpart.ToString()
                Dim dir_pattern = namebase & segmentnumberchars

                If String.IsNullOrWhiteSpace(dir_name) Then
                    dir_name = "."
                End If

                Try
                    foundfiles = Directory.GetFiles(dir_name, dir_pattern)

                Catch ex As Exception
                    Throw New Exception($"Failed enumerating files '{dir_pattern}' in directory '{dir_name}'", ex)

                End Try

                For i = 0 To foundfiles.Length - 1
                    foundfiles(i) = Path.GetFullPath(foundfiles(i))
                Next

                Array.Sort(foundfiles, StringComparer.Ordinal)

            Else

                If File.Exists(FirstFile) Then
                    foundfiles = {FirstFile}
                End If

            End If

            If foundfiles Is Nothing OrElse foundfiles.Length = 0 Then
                Throw New FileNotFoundException("Image file not found", FirstFile)
            End If

            Return foundfiles

#Else

            Dim pathpart = Path.GetDirectoryName(FirstFile)
            Dim filepart = Path.GetFileNameWithoutExtension(FirstFile)
            Dim extension = Path.GetExtension(FirstFile)
            Dim foundfiles As String() = Nothing

            If extension.EndsWith("01", StringComparison.Ordinal) OrElse
                extension.EndsWith("00", StringComparison.Ordinal) Then

                Dim start = extension.Length - 3

                While start >= 0 AndAlso Char.IsDigit(extension, start)
                    start -= 1
                End While

                start += 1

                Dim segmentnumberchars As New String("?"c, extension.Length - start)
                Dim namebase = String.Concat(filepart, extension.Remove(start))
                Dim pathbase = Path.Combine(pathpart, namebase)
                Dim dir_name = pathpart
                Dim dir_pattern = String.Concat(namebase, segmentnumberchars)

                If String.IsNullOrWhiteSpace(dir_name) Then
                    dir_name = "."
                End If

                Try
                    foundfiles = Directory.GetFiles(dir_name, dir_pattern)

                Catch ex As Exception
                    Throw New Exception($"Failed enumerating files '{dir_pattern}' in directory '{dir_name}'", ex)

                End Try

                For i = 0 To foundfiles.Length - 1
                    foundfiles(i) = Path.GetFullPath(foundfiles(i))
                Next

                Array.Sort(foundfiles, StringComparer.Ordinal)

            Else

                If File.Exists(FirstFile) Then
                    foundfiles = {FirstFile}
                End If

            End If

            If foundfiles Is Nothing OrElse foundfiles.Length = 0 Then
                Throw New FileNotFoundException("Image file not found", FirstFile)
            End If

            Return foundfiles
#End If

        End Function

        <Extension>
        Public Sub ConvertToDiscUtilsImage(provider As IDevioProvider, outputImage As String, type As String, OutputImageVariant As String, hashResults As Dictionary(Of String, Byte()), completionPosition As CompletionPosition, cancel As CancellationToken)

            If Not DevioServiceFactory.DiscUtilsInitialized Then

                Throw New NotSupportedException("DiscUtils libraries not available")

            End If

            Using builder = VirtualDisk.CreateDisk(type, OutputImageVariant, outputImage, provider.Length, Geometry.FromCapacity(provider.Length, CInt(provider.SectorSize)), Nothing)

                provider.WriteToSkipEmptyBlocks(builder.Content, ImageConversionIoBufferSize, skipWriteZeroBlocks:=True, hashResults:=hashResults, adjustTargetSize:=False, completionPosition:=completionPosition, cancel:=cancel)

            End Using

        End Sub

        <Extension>
        Public Sub ConvertToRawImage(provider As IDevioProvider, outputImage As String, OutputImageVariant As String, hashResults As Dictionary(Of String, Byte()), completionPosition As CompletionPosition, cancel As CancellationToken)

            Using target As New FileStream(outputImage, FileMode.Create, FileAccess.Write, FileShare.Delete, ImageConversionIoBufferSize)

                If "fixed".Equals(OutputImageVariant, StringComparison.OrdinalIgnoreCase) Then

                ElseIf "dynamic".Equals(OutputImageVariant, StringComparison.OrdinalIgnoreCase) Then

                    If Not RuntimeInformation.IsOSPlatform(OSPlatform.Windows) Then
                        Throw New PlatformNotSupportedException("Sparse files not supported on target platform or OS")
                    End If

                    Try
                        NativeFileIO.SetFileSparseFlag(target.SafeFileHandle, True)

                    Catch ex As Exception
                        Throw New NotSupportedException("Sparse files not supported on target platform or OS", ex)

                    End Try

                Else

                    Throw New ArgumentException($"Value {OutputImageVariant} not supported as output image variant. Valid values are fixed or dynamic.")

                End If

                provider.WriteToSkipEmptyBlocks(target, ImageConversionIoBufferSize, skipWriteZeroBlocks:=True, hashResults:=hashResults, adjustTargetSize:=True, completionPosition:=completionPosition, cancel:=cancel)

            End Using

        End Sub

        <Extension, SupportedOSPlatform(NativeConstants.SUPPORTED_WINDOWS_PLATFORM)>
        Public Sub WriteToPhysicalDisk(provider As IDevioProvider, outputDevice As ReadOnlyMemory(Of Char), completionPosition As CompletionPosition, cancel As CancellationToken)

            Using disk As New DiskDevice(outputDevice, FileAccess.ReadWrite)

                provider.WriteToSkipEmptyBlocks(disk.GetRawDiskStream(), ImageConversionIoBufferSize, skipWriteZeroBlocks:=False, hashResults:=Nothing, adjustTargetSize:=False, completionPosition:=completionPosition, cancel:=cancel)

            End Using

        End Sub

        <Extension>
        Public Sub ConvertToLibEwfImage(provider As IDevioProvider, outputImage As String, hashResults As Dictionary(Of String, Byte()), completionPosition As CompletionPosition, cancel As CancellationToken)

            Dim imaging_parameters As New DevioProviderLibEwf.ImagingParameters With {
                .MediaSize = CULng(provider.Length),
                .BytesPerSector = provider.SectorSize
            }

            If RuntimeInformation.IsOSPlatform(OSPlatform.Windows) Then

                Dim physical_disk_handle = TryCast(TryCast(provider, DevioProviderFromStream)?.BaseStream, FileStream)?.SafeFileHandle

                If physical_disk_handle IsNot Nothing Then

                    Dim storageproperties = NativeFileIO.GetStorageStandardProperties(physical_disk_handle)
                    If storageproperties.HasValue Then

                        imaging_parameters.StorageStandardProperties = storageproperties.Value
                        Trace.WriteLine($"Source disk vendor '{imaging_parameters.StorageStandardProperties.VendorId}' model '{imaging_parameters.StorageStandardProperties.ProductId}', serial number '{imaging_parameters.StorageStandardProperties.SerialNumber}'")

                    End If

                End If

            End If

            Using target As New DevioProviderLibEwf({Path.ChangeExtension(outputImage, Nothing)}, DevioProviderLibEwf.AccessFlagsWrite)

                target.SetOutputParameters(imaging_parameters)

                Using stream As New Client.DevioDirectStream(target, ownsProvider:=False)

                    provider.WriteToSkipEmptyBlocks(stream, ImageConversionIoBufferSize, skipWriteZeroBlocks:=False, hashResults:=hashResults, adjustTargetSize:=False, completionPosition:=completionPosition, cancel:=cancel)

                End Using

                For Each hash In hashResults
                    target.SetOutputHashParameter(hash.Key, hash.Value)
                Next

            End Using

        End Sub

        <Extension>
        Public Sub WriteToSkipEmptyBlocks(source As IDevioProvider, target As Stream, buffersize As Integer, skipWriteZeroBlocks As Boolean, adjustTargetSize As Boolean, hashResults As Dictionary(Of String, Byte()), completionPosition As CompletionPosition, cancel As CancellationToken)

            Using hashProviders As New DisposableDictionary(Of String, HashAlgorithm)(StringComparer.OrdinalIgnoreCase)

                If hashResults IsNot Nothing Then
                    For Each hashName In hashResults.Keys
                        Dim hashProvider = HashAlgorithm.Create(hashName)
                        hashProvider.Initialize()
                        hashProviders.Add(hashName, hashProvider)
                    Next
                End If

                Dim buffer(0 To buffersize - 1) As Byte

                Dim count = 0

                Dim source_position = 0L

                Do

                    cancel.ThrowIfCancellationRequested()

                    Dim length_to_read = CInt(Math.Min(buffer.Length, source.Length - source_position))

                    If length_to_read = 0 Then

                        Exit Do

                    End If

                    count = source.Read(buffer, 0, length_to_read, source_position)

                    If count = 0 Then

                        Throw New IOException($"Read error, {length_to_read} bytes from {source_position}")

                    End If

                    Parallel.ForEach(hashProviders.Values, Function(hashProvider) hashProvider.TransformBlock(buffer, 0, count, Nothing, 0))

                    source_position += count

                    If completionPosition IsNot Nothing Then
                        completionPosition.LengthComplete = source_position
                    End If

                    If skipWriteZeroBlocks AndAlso buffer.IsBufferZero() Then

                        target.Seek(count, SeekOrigin.Current)

                    Else

                        cancel.ThrowIfCancellationRequested()

                        target.Write(buffer, 0, count)

                    End If

                Loop

                If adjustTargetSize AndAlso
                    target.Length <> target.Position Then

                    target.SetLength(target.Position)

                End If

                For Each hashProvider In hashProviders
                    hashProvider.Value.TransformFinalBlock(Array.Empty(Of Byte)(), 0, 0)
                    hashResults(hashProvider.Key) = hashProvider.Value.Hash
                Next

            End Using

        End Sub

    End Module

End Namespace
