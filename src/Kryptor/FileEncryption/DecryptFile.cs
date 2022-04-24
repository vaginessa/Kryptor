﻿/*
    Kryptor: A simple, modern, and secure encryption and signing tool.
    Copyright (C) 2020-2022 Samuel Lucas

    This program is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program. If not, see https://www.gnu.org/licenses/.
*/

using System;
using System.IO;
using System.Text;
using System.Security.Cryptography;
using Sodium;
using ChaCha20BLAKE2;

namespace Kryptor;

public static class DecryptFile
{
    public static void Decrypt(FileStream inputFile, string outputFilePath, byte[] ephemeralPublicKey, byte[] keyEncryptionKey)
    {
        var dataEncryptionKey = new byte[Constants.EncryptionKeyLength];
        try
        {
            byte[] nonce = FileHeaders.ReadNonce(inputFile);
            byte[] encryptedFileHeader = FileHeaders.ReadEncryptedHeader(inputFile);
            byte[] fileHeader = DecryptFileHeader(inputFile, ephemeralPublicKey, encryptedFileHeader, nonce, keyEncryptionKey);
            int paddingLength = BitConversion.ToInt32(Arrays.Copy(fileHeader, sourceIndex: 0, Constants.IntBitConverterLength));
            bool isDirectory = BitConverter.ToBoolean(Arrays.Copy(fileHeader, sourceIndex: Constants.IntBitConverterLength, length: Constants.BoolBitConverterLength));
            int fileNameLength = BitConversion.ToInt32(Arrays.Copy(fileHeader, Constants.IntBitConverterLength + Constants.BoolBitConverterLength, Constants.IntBitConverterLength));
            byte[] fileName = fileNameLength == 0 ? Array.Empty<byte>() : Arrays.Copy(fileHeader, fileHeader.Length - dataEncryptionKey.Length - Constants.FileNameHeaderLength, fileNameLength);
            dataEncryptionKey = Arrays.Copy(fileHeader, fileHeader.Length - dataEncryptionKey.Length, Constants.EncryptionKeyLength);
            CryptographicOperations.ZeroMemory(fileHeader);
            using (var outputFile = new FileStream(outputFilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, Constants.FileStreamBufferSize, FileOptions.SequentialScan))
            {
                nonce = Utilities.Increment(nonce);
                DecryptChunks(inputFile, outputFile, nonce, dataEncryptionKey, paddingLength);
            }
            inputFile.Dispose();
            Globals.SuccessfulCount += 1;
            if (fileNameLength != 0) { outputFilePath = FileHandling.RenameFile(outputFilePath, Encoding.UTF8.GetString(fileName)); }
            if (isDirectory) { FileHandling.ExtractZipFile(outputFilePath); }
            if (Globals.Overwrite) { FileHandling.DeleteFile(inputFile.Name); }
        }
        catch (Exception ex) when (ExceptionFilters.Cryptography(ex))
        {
            CryptographicOperations.ZeroMemory(dataEncryptionKey);
            if (ex is not ArgumentException) { FileHandling.DeleteFile(outputFilePath); }
            throw;
        }
    }

    private static byte[] DecryptFileHeader(FileStream inputFile, byte[] ephemeralPublicKey, byte[] encryptedFileHeader, byte[] nonce, byte[] keyEncryptionKey)
    {
        try
        {
            byte[] ciphertextLength = BitConversion.GetBytes(inputFile.Length - Constants.FileHeadersLength);
            byte[] magicBytes = FileHandling.ReadFileHeader(inputFile, offset: 0, Constants.KryptorMagicBytes.Length);
            byte[] formatVersion = FileHandling.ReadFileHeader(inputFile, Constants.KryptorMagicBytes.Length, Constants.EncryptionVersion.Length);
            FileHeaders.ValidateFormatVersion(formatVersion, Constants.EncryptionVersion);
            byte[] additionalData = Arrays.Concat(ciphertextLength, magicBytes, formatVersion, ephemeralPublicKey);
            return XChaCha20BLAKE2b.Decrypt(encryptedFileHeader, nonce, keyEncryptionKey, additionalData);
        }
        catch (CryptographicException ex)
        {
            throw new ArgumentException("Incorrect password/key, or this file has been tampered with.", ex);
        }
    }

    private static void DecryptChunks(Stream inputFile, Stream outputFile, byte[] nonce, byte[] dataEncryptionKey, int paddingLength)
    {
        var ciphertextChunk = new byte[Constants.CiphertextChunkLength];
        inputFile.Seek(Constants.FileHeadersLength, SeekOrigin.Begin);
        while (inputFile.Read(ciphertextChunk, offset: 0, ciphertextChunk.Length) > 0)
        {
            byte[] plaintextChunk = XChaCha20BLAKE2b.Decrypt(ciphertextChunk, nonce, dataEncryptionKey);
            nonce = Utilities.Increment(nonce);
            outputFile.Write(plaintextChunk, offset: 0, plaintextChunk.Length);
        }
        outputFile.SetLength(outputFile.Length - paddingLength);
        CryptographicOperations.ZeroMemory(dataEncryptionKey);
    }
}