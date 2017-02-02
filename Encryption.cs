using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;


namespace BLL
{
	public class TripleDESEncryption
	{
		private static byte[] key = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24 };
		private static byte[] iv = { 65, 110, 68, 26, 69, 178, 200, 219 };

		//private static byte[] key = { 43, 12, 8, 255, 193, 173, 121, 201, 85, 9, 1, 250, 21, 99, 181, 10, 232, 54, 21, 154, 66, 82, 111, 74};
		//private static byte[] iv = { 173, 19, 68, 235, 195, 98, 131, 87 };


		public static string Encrypt(string strToEncode)
		{
			// Declare a UTF8Encoding object so we may use the GetByte method to transform the plainText into a Byte array. 
			UTF8Encoding utf8encoder = new UTF8Encoding();
			byte[] inputInBytes = utf8encoder.GetBytes(strToEncode);

			// Create a new TripleDES service provider 
			TripleDESCryptoServiceProvider tdesProvider = new TripleDESCryptoServiceProvider();

			// The ICryptTransform interface uses the TripleDES crypt provider along with encryption key and init vector information 
			ICryptoTransform cryptoTransform = tdesProvider.CreateEncryptor(key, iv);

			// All cryptographic functions need a stream to output the encrypted information. Here we declare a memory stream for this purpose. 
			MemoryStream encryptedStream = new MemoryStream();
			CryptoStream cryptStream = new CryptoStream(encryptedStream, cryptoTransform, CryptoStreamMode.Write);

			// Write the encrypted information to the stream. Flush the information 
			// when done to ensure everything is out of the buffer. 
			cryptStream.Write(inputInBytes, 0, inputInBytes.Length);
			cryptStream.FlushFinalBlock();
			encryptedStream.Position = 0;

			// Read the stream back into a Byte array and return it to the calling method. 
			byte[] result = new byte[encryptedStream.Length];
			encryptedStream.Read(result, 0, Convert.ToInt32(encryptedStream.Length));

			cryptStream.Close();

			// REturn byte array as string
			return System.Convert.ToBase64String(result);
		}


		public static string Decrypt(string strToDecode)
		{
			// Convert input string to array of bytes
			byte[] inputInBytes = System.Convert.FromBase64String(strToDecode);

			// UTFEncoding is used to transform the decrypted Byte Array information back into a string. 
			UTF8Encoding utf8encoder = new UTF8Encoding();
			TripleDESCryptoServiceProvider tdesProvider = new TripleDESCryptoServiceProvider();

			// As before we must provide the encryption/decryption key along with the init vector. 
			ICryptoTransform cryptoTransform = tdesProvider.CreateDecryptor(key, iv);

			// Provide a memory stream to decrypt information into 
			MemoryStream decryptedStream = new MemoryStream();
			CryptoStream cryptStream = new CryptoStream(decryptedStream, cryptoTransform, CryptoStreamMode.Write);
			cryptStream.Write(inputInBytes, 0, inputInBytes.Length);
			cryptStream.FlushFinalBlock();
			decryptedStream.Position = 0;

			// Read the memory stream and convert it back into a string 
			byte[] result = new byte[decryptedStream.Length];
			decryptedStream.Read(result, 0, Convert.ToInt32(decryptedStream.Length));
			cryptStream.Close();

			UTF8Encoding myutf = new UTF8Encoding();

			return myutf.GetString(result);
		}
	}
}