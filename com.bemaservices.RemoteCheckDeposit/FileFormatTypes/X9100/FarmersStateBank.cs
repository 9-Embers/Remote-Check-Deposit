using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Text;

using com.bemaservices.RemoteCheckDeposit.Records.X9100;

using DotLiquid.Tags;

using Rock;
using Rock.Attribute;
using Rock.Model;
using Rock.Plugin.HotFixes;

namespace com.bemaservices.RemoteCheckDeposit.FileFormatTypes
{
    /// <summary>
    /// Defines the basic functionality of any component that will be exporting using the X9.100
    /// DSTU standard.
    /// </summary>
    [Description( "Processes a batch export for Farmers State Bank.  This exports is built on X9.100-187 Standard " )]
    [Export(typeof(FileFormatTypeComponent))]
    [ExportMetadata("ComponentName", "Farmers State Bank")]

    [EncryptedTextField( "Deposit Routing Number", "The routing number to be used on Credit Detail record (25)", true, key: "DepositRoutingNumber", order: 12 )]
    [EncryptedTextField( "Credit Transaction Number", "The credit transaction number to be included on Credit Detail record (25). Farmers should provide this number.", true, key: "CreditTransactionNumberr", order: 13 )]
    [CodeEditorField( "Deposit Slip Template", "The template for the deposit slip that will be generated. <span class='tip tip-lava'></span>",
        Rock.Web.UI.Controls.CodeEditorMode.Lava,
        defaultValue: @"
{{ FileFormat | Attribute:'OriginName' }}
Account Number: {{ FileFormat | Attribute:'AccountNumber' }}
Created On {{ Date | Date:'MM-dd-yyyy'}} at {{ Date | Date:'HH:mm' }} by Teller
Deposited {{ Transactions | Format:'N0' }} checks totaling {{ Amount | FormatAsCurrency }}
", order: 14 )]

    class FarmersStateBank : X9100DSTU
    {
        #region Private Members

        #endregion

        #region System Setting Keys

        /// <summary>
        /// The system setting for the next cash header identifier. These should never be
        /// repeated. Ever.
        /// </summary>
        protected const string SystemSettingNextCashHeaderId = "FarmersStateBank.NextCashHeaderId";

        /// <summary>
        /// The system setting that contains the last file modifier we used.
        /// </summary>
        protected const string SystemSettingLastFileModifier = "FarmersStateBank.LastFileModifier";

        /// <summary>
        /// The last item sequence number used for items.
        /// </summary>
        protected const string LastItemSequenceNumberKey = "FarmersStateBank.LastItemSequenceNumber";

        #endregion

        #region Export Batches

        public override Stream ExportBatches( ExportOptions options, out List<string> errorMessages )
        {
            var records = new List<Record>();

            errorMessages = new List<string>();

            //
            // Get all the transactions that will be exported from these batches.
            //
            var transactions = options.Batches.SelectMany( b => b.Transactions )
                .OrderBy( t => t.ProcessedDateTime )
                .ThenBy( t => t.Id )
                .ToList();

            //
            // Perform error checking to ensure that all the transactions in these batches
            // are of the proper currency type.
            //
            List<Guid> currencyGuids = GetAttributeValue( options.FileFormat, "CurrencyTypes" ).SplitDelimitedValues().AsGuidList();
            if ( !currencyGuids.Any() )
            {
                //Add the default check option if nothing is selected
                currencyGuids.Add( Guid.Parse( "8B086A19-405A-451F-8D44-174E92D6B402" ) );
            }
            List<int> currencyIds = new List<int>();
            foreach ( Guid guid in currencyGuids )
            {
                currencyIds.Add( Rock.Web.Cache.DefinedValueCache.Get( guid ).Id );
            }

            if ( transactions.Any( t => !currencyIds.Contains( t.FinancialPaymentDetail.CurrencyTypeValueId ?? -1 ) ) )
            {
                errorMessages.Add( "One or more transactions is not of a selected Check type." );
                throw new Exception( "One or more transactions is not of a selected Check type." );
            }

            //
            // Generate all the X9.100 records for this set of transactions.
            //
            records.Add( GetFileHeaderRecord( options ) );
            records.Add( GetCashLetterHeaderRecord( options ) );
            records.AddRange( GetBundleRecords( options, transactions ) );
            records.Add( GetCashLetterControlRecord( options, records ) );
            records.Add( GetFileControlRecord( options, records ) );

            // Convert to Farmer's records
            var farmersRecords = new List<Record>();
            foreach( var record in records )
            {
                switch ( record.RecordType ) {
                    case 25:
                        farmersRecords.Add( new Records.X9100.FarmersStateBank.CheckDetail( record as CheckDetail )  );
                        break;
                    case 70:
                        farmersRecords.Add( new Records.X9100.FarmersStateBank.BundleControl( record as BundleControl ) );
                        break;
                    case 90:
                        farmersRecords.Add( new Records.X9100.FarmersStateBank.CashLetterControl( record as CashLetterControl ) );
                        break;
                    case 99:
                        farmersRecords.Add( new Records.X9100.FarmersStateBank.FileControl( record as FileControl ) );
                        break;
                    default:
                        farmersRecords.Add( record );
                        break;
                }
            }

            // If testing write the records to a X9100.txt file in App_Data/Logs
            bool isTestMode = GetAttributeValue( options.FileFormat, "TestMode" ).AsBoolean( true );
            if ( isTestMode )
            {
                try
                {
                    string directory = AppDomain.CurrentDomain.BaseDirectory;
                    directory = Path.Combine( directory, "App_Data", "Logs" );

                    if ( !Directory.Exists( directory ) )
                    {
                        Directory.CreateDirectory( directory );
                    }

                    string filePath = Path.Combine( directory, "X9100.txt" );
                    using ( var writer = new StreamWriter( filePath, false ) )
                    {
                        foreach ( var record in farmersRecords )
                        {
                            WriteTextRecord( record, writer );
                            writer.WriteLine();
                        }
                    }
                }
                catch
                {
                    // Intentionally ignored, don't error if we couldn't log.
                }
            }

            //
            // Encode all the records into a memory stream so that it can be saved to a file
            // by the caller.
            //
            var stream = new MemoryStream();

            WritePreContent( options, stream );

            using ( var writer = new BinaryWriter( stream, System.Text.Encoding.UTF8, true ) )
            {
                foreach ( var record in farmersRecords )
                {
                    WriteRecord( record, writer );
                }
            }

            stream.Position = 0;

            return stream;
        }

        #endregion

        #region File Records

        /// <summary>
        /// Gets the file header record (type 01).
        /// </summary>
        /// <param name="options">Export options to be used by the component.</param>
        /// <returns>
        /// A FileHeader record.
        /// </returns>
        protected override FileHeader GetFileHeaderRecord( ExportOptions options )
        {
            var institutionRoutingNumber = Rock.Security.Encryption.DecryptString( GetAttributeValue( options.FileFormat, "InstitutionRoutingNumber" ) );

            var header = base.GetFileHeaderRecord( options );

            header.ImmediateOriginRoutingNumber = institutionRoutingNumber;
            header.StandardLevel = 03;

            //
            // The combination of the following fields must be unique:
            // DestinationRoutingNumber + OriginatingRoutingNumber + CreationDateTime + FileIdModifier
            //
            // If the last file we sent has the same routing numbers and creation date time then
            // increment the file id modifier.
            //
            var fileIdModifier = "A";
            header.CountryCode = string.Empty;
            var hashText = header.ImmediateDestinationRoutingNumber + header.ImmediateOriginRoutingNumber + header.FileCreationDateTime.ToString( "yyyyMMdd" );
            var hash = HashString( hashText );

            //
            // find the last modifier, if there was one.
            //
            var lastModifier = GetSystemSetting( SystemSettingLastFileModifier );
            if ( !string.IsNullOrWhiteSpace( lastModifier ) )
            {
                var components = lastModifier.Split( '|' );

                if ( components.Length == 2 )
                {
                    //
                    // If the modifier is for the same file, increment the file modifier.
                    //
                    if ( components[0] == hash )
                    {
                        fileIdModifier = ( ( char ) ( components[1][0] + 1 ) ).ToString();

                        //
                        // If we have done more than 26 files today, assume we are testing and start back at 'A'.
                        //
                        if ( fileIdModifier[0] > 'Z' )
                        {
                            fileIdModifier = "A";
                        }
                    }
                }
            }

            header.FileIdModifier = fileIdModifier;
            SetSystemSetting( SystemSettingLastFileModifier, string.Join( "|", hash, fileIdModifier ) );

            return header;
        }

        /// <summary>
        /// Gets the cash letter header record (type 10).
        /// </summary>
        /// <param name="options">Export options to be used by the component.</param>
        /// <returns>
        /// A CashLetterHeader record.
        /// </returns>
        protected override CashLetterHeader GetCashLetterHeaderRecord( ExportOptions options )
        {
            var header = base.GetCashLetterHeaderRecord( options );
            header.EceInstitutionRoutingNumber = header.DestinationRoutingNumber;
            header.FedWorkType = "C";

            int cashHeaderId = GetSystemSetting( SystemSettingNextCashHeaderId ).AsIntegerOrNull() ?? 1;
            header.CashLetterId = cashHeaderId.ToString( "D8" );
            SetSystemSetting( SystemSettingNextCashHeaderId, ( cashHeaderId + 1 ).ToString() );

            return header;
        }

        /// <summary>
        /// Gets the bundle header record (type 20).
        /// </summary>
        /// <param name="options">Export options to be used by the component.</param>
        /// <param name="bundleIndex">Number of existing bundle records in the cash letter.</param>
        /// <returns>A BundleHeader record.</returns>
        protected override BundleHeader GetBundleHeader(ExportOptions options, int bundleIndex)
        {
            var header = base.GetBundleHeader( options, bundleIndex );
            header.ReturnLocationRoutingNumber = string.Empty;
            header.SequenceNumber = header.SequenceNumber.PadLeft( 4, '0' );
            return header;
        }

        /// <summary>
        /// Gets the item detail records (type 25)
        /// </summary>
        /// <param name="options">Export options to be used by the component.</param>
        /// <param name="transaction">The transaction being deposited.</param>
        /// <returns>A collection of records.</returns>
        protected override List<Record> GetItemDetailRecords(ExportOptions options, FinancialTransaction transaction)
        {
            var records = base.GetItemDetailRecords( options, transaction );
            var sequenceNumber = GetNextItemSequenceNumber();

            var checkDetail = records.Where( r => r.RecordType == 25 ).Cast<CheckDetail>().FirstOrDefault();
            checkDetail.ClientInstitutionItemSequenceNumber = sequenceNumber.ToString( "000000000000000" );
            checkDetail.CheckDetailRecordAddendumCount = 0;

            //return records;
            // Type 26 is not needed
            return records.Where( r => r.RecordType == 25 ).ToList();
        }

        /// <summary>
        /// Gets the image records (type 50 and 52)
        /// </summary>
        /// <param name="options"></param>
        /// <param name="transaction"></param>
        /// <param name="image"></param>
        /// <param name="isFront"></param>
        /// <returns></returns>
        protected override List<Record> GetImageRecords( ExportOptions options, FinancialTransaction transaction, FinancialTransactionImage image, bool isFront )
        {
            var records = base.GetImageRecords( options, transaction, image, isFront );

            var detail = records.Where( r => r.RecordType == 50 ).Cast<ImageViewDetail>().FirstOrDefault();
            if ( detail != null )
            {
                detail.DigitalSignatureMethod = null;
                detail.SecurityKeySize = null;
                detail.StartOfProtectedData = null;
                detail.LengthOfProtectedData = null;
            }

            var data = records.Where( r => r.RecordType == 52 ).Cast<ImageViewData>().FirstOrDefault();
            if ( data != null )
            {
                data.ClientInstitutionItemSequenceNumber = GetSystemSetting( LastItemSequenceNumberKey ).PadLeft( 15, '0' );
                data.ClippingCoordinateH1 = null;
                data.ClippingCoordinateH2 = null;
                data.ClippingCoordinateV1 = null;
                data.ClippingCoordinateV2 = null;
            }

            return records;
        }

        /// <summary>
        /// Gets the credit detail deposit record (type 61).
        /// </summary>
        /// <param name="options">Export options to be used by the component.</param>
        /// <param name="bundleIndex">Number of existing bundle records in the cash letter.</param>
        /// <param name="transactions">The transactions associated with this deposit.</param>
        /// <returns>
        /// A collection of records.
        /// </returns>
        protected override List<Record> GetCreditDetailRecords( ExportOptions options, int bundleIndex, List<FinancialTransaction> transactions )
        {
            var depositRoutingNumber = Rock.Security.Encryption.DecryptString( GetAttributeValue( options.FileFormat, "DepositRoutingNumber" ) );
            var accountNumber = Rock.Security.Encryption.DecryptString( GetAttributeValue( options.FileFormat, "AccountNumber" ) );
            var creditTransactionNumber = Rock.Security.Encryption.DecryptString( GetAttributeValue( options.FileFormat, "CreditTransactionNumberr" ) );

            var records = new List<Record>();

            var creditDetail = new CheckDetail //Type 25
            {
                PayorBankRoutingNumber = depositRoutingNumber.Substring( 0, 8 ),
                PayorBankRoutingNumberCheckDigit = depositRoutingNumber.Substring( 8, 1 ),
                OnUs = $"{accountNumber}/{creditTransactionNumber}".PadLeft( 20, ' ' ),
                ItemAmount = transactions.Sum( t => t.TotalAmount ),
                ClientInstitutionItemSequenceNumber = GetNextItemSequenceNumber().ToString( "000000000000000" ),
                BankOfFirstDepositIndicator = "U",
                CheckDetailRecordAddendumCount = 00,
                DocumentationTypeIndicator = "G"
            };

            records.Add(  creditDetail );

            for ( int i = 0; i < 2; i++ )
            {
                using ( var ms = GetDepositSlipImage( options, i == 0, transactions ) )
                {
                    //
                    // Get the Image View Detail record (type 50).
                    //
                    var detail = new ImageViewDetail
                    {
                        ImageIndicator = 1,
                        ImageCreatorRoutingNumber = depositRoutingNumber,
                        ImageCreatorDate = options.ExportDateTime,
                        ImageViewFormatIndicator = 0,
                        CompressionAlgorithmIdentifier = 0,
                        SideIndicator = i,
                        ViewDescriptor = 0,
                        DigitalSignatureIndicator = 0
                    };

                    //
                    // Get the Image View Data record (type 52).
                    //
                    var data = new ImageViewData
                    {
                        InstitutionRoutingNumber = depositRoutingNumber,
                        BundleBusinessDate = options.BusinessDateTime,
                        ClientInstitutionItemSequenceNumber = creditDetail.ClientInstitutionItemSequenceNumber,
                        ClippingOrigin = 0,
                        ImageData = ms.ReadBytesToEnd()
                    };

                    detail.DataSize = ( int ) data.ImageData.Length;

                    records.Add( detail );
                    records.Add( data );
                }
            }

            return records;
        }

        protected override BundleControl GetBundleControl( ExportOptions options, List<Record> records )
        {
            var control = base.GetBundleControl( options, records );
            control.MICRValidTotalAmount = null;
            return control;
        }

        /// <summary>
        /// Gets the Cash Leter Control Record (type 90)
        /// </summary>
        /// <param name="options"></param>
        /// <param name="records"></param>
        /// <returns></returns>
        protected override CashLetterControl GetCashLetterControlRecord( ExportOptions options, List<Record> records )
        {
            var control = base.GetCashLetterControlRecord( options, records );
            control.ECEInstitutionName = GetAttributeValue( options.FileFormat, "ContactName" );
            return control;
        }

        #endregion

        #region Methods

        /// <summary>
        /// Gets the next item sequence number.
        /// </summary>
        /// <returns>An integer that identifies the unique item sequence number that can be used.</returns>
        protected int GetNextItemSequenceNumber()
        {
            int lastSequence = GetSystemSetting(LastItemSequenceNumberKey).AsIntegerOrNull() ?? 0;
            int nextSequence = lastSequence + 1;

            SetSystemSetting(LastItemSequenceNumberKey, nextSequence.ToString());

            return nextSequence;
        }

        protected virtual Stream GetDepositSlipImage( ExportOptions options, bool isFrontSide, List<FinancialTransaction> transactions )
        {
            var bitmap = new System.Drawing.Bitmap( 1200, 550 );
            var g = System.Drawing.Graphics.FromImage( bitmap );

            var depositSlipTemplate = GetAttributeValue( options.FileFormat, "DepositSlipTemplate" );
            var mergeFields = new Dictionary<string, object>
            {
                { "FileFormat", options.FileFormat },
                { "Date", options.ExportDateTime.ToISO8601DateString() },
                { "Transactions", transactions.Count() },
                { "Amount", transactions.Sum( t => t.TotalAmount ) }
            };
            var depositSlipText = depositSlipTemplate.ResolveMergeFields( mergeFields, null );

            //
            // Ensure we are opague with white.
            //
            g.FillRectangle( System.Drawing.Brushes.White, new System.Drawing.Rectangle( 0, 0, 1200, 550 ) );

            if ( isFrontSide )
            {
                g.DrawString( depositSlipText,
                    new System.Drawing.Font( "Tahoma", 30 ),
                    System.Drawing.Brushes.Black,
                    new System.Drawing.PointF( 50, 50 ) );
            }

            g.Flush();

            //
            // Ensure the DPI is correct.
            //
            bitmap.SetResolution( 200, 200 );

            //
            // Compress using TIFF, CCITT Group 4 format.
            //
            var codecInfo = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders()
                .Where( c => c.MimeType == "image/tiff" )
                .First();
            var parameters = new System.Drawing.Imaging.EncoderParameters( 1 );
            parameters.Param[0] = new System.Drawing.Imaging.EncoderParameter( System.Drawing.Imaging.Encoder.Compression, ( long ) System.Drawing.Imaging.EncoderValue.CompressionCCITT4 );

            var ms = new MemoryStream();
            bitmap.Save( ms, codecInfo, parameters );
            ms.Position = 0;

            return ms;

        }

        /// <summary>
        /// Hashes the string with SHA256.
        /// </summary>
        /// <param name="contents">The contents to be hashed.</param>
        /// <returns>A hex representation of the hash.</returns>
        protected string HashString( string contents )
        {
            byte[] byteContents = Encoding.Unicode.GetBytes( contents );

            var hash = new System.Security.Cryptography.SHA256CryptoServiceProvider().ComputeHash( byteContents );

            return string.Join( "", hash.Select( b => b.ToString( "x2" ) ).ToArray() );
        }

        #endregion

    }
}
