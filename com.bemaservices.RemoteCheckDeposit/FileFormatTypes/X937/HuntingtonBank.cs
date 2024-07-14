using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Text;

using com.bemaservices.RemoteCheckDeposit.Records.X937;

using Rock;
using Rock.Attribute;
using Rock.Model;

namespace com.bemaservices.RemoteCheckDeposit.FileFormatTypes
{
    /// <summary>
    /// Defines the basic functionality of any component that will be exporting using the X9.37
    /// DSTU standard.
    /// </summary>
    [Description( "Processes a batch export for Huntington Bank." )]
    [Export( typeof( FileFormatTypeComponent ) )]
    [ExportMetadata( "ComponentName", "Huntington Bank" )]

    [EncryptedTextField( "Bank of First Deposit (BOFD) Routing Number", "", true, key: "BOFDRoutingNumber", order: 11 )]

    [EncryptedTextField( "Deposit Routing Number", "The routing number to be used on Credit Detail record (25)", true, key: "DepositRoutingNumber", order: 12 )]
    [CodeEditorField( "Deposit Slip Template", "The template for the deposit slip that will be generated. <span class='tip tip-lava'></span>",
        Rock.Web.UI.Controls.CodeEditorMode.Lava,
        defaultValue: @"
{{ FileFormat | Attribute:'OriginName' }}
Account Number: {{ FileFormat | Attribute:'AccountNumber' }}
Created On {{ Date | Date:'MM-dd-yyyy'}} at {{ Date | Date:'HH:mm' }} by Teller
Deposited {{ Transactions | Format:'N0' }} checks totaling {{ Amount | FormatAsCurrency }}
", order: 12 )]

    public class HuntingtonBank : X937DSTU
    {
        #region System Setting Keys

        /// <summary>
        /// The system setting for the next cash header identifier. These should never be
        /// repeated. Ever.
        /// </summary>
        protected const string SystemSettingNextCashHeaderId = "HuntingtonBank.NextCashHeaderId";

        /// <summary>
        /// The system setting that contains the last file modifier we used.
        /// </summary>
        protected const string SystemSettingLastFileModifier = "HuntingtonBank.LastFileModifier";

        /// <summary>
        /// The last item sequence number used for items.
        /// </summary>
        protected const string LastItemSequenceNumberKey = "HuntingtonBank.LastItemSequenceNumber";

        #endregion

        /// <summary>
        /// Gets the next item sequence number.
        /// </summary>
        /// <returns>An integer that identifies the unique item sequence number that can be used.</returns>
        protected int GetNextItemSequenceNumber()
        {
            int lastSequence = GetSystemSetting( LastItemSequenceNumberKey ).AsIntegerOrNull() ?? 0;
            int nextSequence = lastSequence + 1;

            SetSystemSetting( LastItemSequenceNumberKey, nextSequence.ToString() );

            return nextSequence;
        }

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

            header.CountryCode = string.Empty;

            return header;
        }

        protected override BundleHeader GetBundleHeader( ExportOptions options, int bundleIndex )
        {
            var header = base.GetBundleHeader( options, bundleIndex );
            header.ReturnLocationRoutingNumber = string.Empty;
            return header;
        }

        protected override BundleControl GetBundleControl( ExportOptions options, List<Record> records )
        {
            var control = base.GetBundleControl( options, records );
            control.MICRValidTotalAmount = null;
            return control;
        }

        protected override CashLetterControl GetCashLetterControlRecord( ExportOptions options, List<Record> records )
        {
            var cashLeterControl = base.GetCashLetterControlRecord( options, records );
            cashLeterControl.SettlementDate = null;
            return cashLeterControl;
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
            int cashHeaderId = GetSystemSetting( SystemSettingNextCashHeaderId ).AsIntegerOrNull() ?? 1;

            var header = base.GetCashLetterHeaderRecord( options );
            header.ID = cashHeaderId.ToString( "D8" );
            SetSystemSetting( SystemSettingNextCashHeaderId, ( cashHeaderId + 1 ).ToString() );

            return header;
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
            var institutionRoutingNumber = Rock.Security.Encryption.DecryptString( GetAttributeValue( options.FileFormat, "InstitutionRoutingNumber" ) );
            var depositRoutingNumber = Rock.Security.Encryption.DecryptString( GetAttributeValue( options.FileFormat, "DepositRoutingNumber" ) );
            var accountNumber = Rock.Security.Encryption.DecryptString( GetAttributeValue( options.FileFormat, "AccountNumber" ) );

            var records = new List<Record>();

            var creditDetail = new CheckDetail //Type 25
            {
                PayorBankRoutingNumber = depositRoutingNumber.Substring( 0, 8 ),
                PayorBankRoutingNumberCheckDigit = depositRoutingNumber.Substring( 8, 1 ),
                OnUs = ( accountNumber + "/" ).PadLeft( 20, ' ' ),
                ItemAmount = transactions.Sum( t => t.TotalAmount ),
                ClientInstitutionItemSequenceNumber = GetNextItemSequenceNumber().ToString( "000000000000000" ),
                BankOfFirstDepositIndicator = "U",
                CheckDetailRecordAddendumCount = 00,
                DocumentationTypeIndicator = "G"
            };

            records.Add( creditDetail );

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
                        ImageCreatorRoutingNumber = institutionRoutingNumber,
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
                        InstitutionRoutingNumber = institutionRoutingNumber,
                        BundleBusinessDate = options.BusinessDateTime,
                        ClientInstitutionItemSequenceNumber = creditDetail.ClientInstitutionItemSequenceNumber,
                        ClippingOrigin = 0,
                        ImageData = ms.ReadBytesToEnd()
                    };

                    detail.DataSize = (int)data.ImageData.Length;

                    records.Add( detail );
                    records.Add( data );
                }
            }

            return records;
        }

        /// <summary>
        /// Gets the records that identify a single check being deposited.
        /// </summary>
        /// <param name="options">Export options to be used by the component.</param>
        /// <param name="transaction">The transaction to be deposited.</param>
        /// <returns>
        /// A collection of records.
        /// </returns>
        protected override List<Record> GetItemRecords( ExportOptions options, FinancialTransaction transaction )
        {
            var records = base.GetItemRecords( options, transaction );
            var sequenceNumber = GetNextItemSequenceNumber();

            //
            // Modify the Check Detail Record and Check Image Data records to have
            // a unique item sequence number.
            //
            var checkDetail = records.Where( r => r.RecordType == 25 ).Cast<CheckDetail>().FirstOrDefault();
            checkDetail.ClientInstitutionItemSequenceNumber = sequenceNumber.ToString( "000000000000000" );
            checkDetail.ElectronicReturnAcceptanceIndicator = "0";
            checkDetail.MICRValidIndicator = 1; //Added 9/27/19 due to feedback from 5/3
            checkDetail.BankOfFirstDepositIndicator = "U";
            checkDetail.ArchiveTypeIndicator = string.Empty;


            //Modify Check Detail Adden A
            var checkDetailA = records.Where( r => r.RecordType == 26 ).Cast<CheckDetailAddendumA>().FirstOrDefault();
            checkDetailA.BankOfFirstDepositRoutingNumber = Rock.Security.Encryption.DecryptString( GetAttributeValue( options.FileFormat, "BOFDRoutingNumber" ) );
            checkDetailA.TruncationIndicator = "Y";
            checkDetailA.BankOfFirstDepositItemSequenceNumber = sequenceNumber.ToString("000000000000000");
            checkDetailA.BankOfFirstDepositAccountNumber = Rock.Security.Encryption.DecryptString( GetAttributeValue( options.FileFormat, "AccountNumber" ) ).PadLeft( 18, ' ' );
            checkDetailA.BankOfFirstDepositCorrectionIndicator = "0";
            foreach ( var imageData in records.Where( r => r.RecordType == 52 ).Cast<dynamic>() )
            {
                imageData.ClientInstitutionItemSequenceNumber = sequenceNumber.ToString( "000000000000000" );
            }

            return records;
        }

        /// <summary>
        /// Gets the image record for a specific transaction image (type 50 and 52).
        /// </summary>
        /// <param name="options">Export options to be used by the component.</param>
        /// <param name="transaction">The transaction being deposited.</param>
        /// <param name="image">The check image scanned by the scanning application.</param>
        /// <param name="isFront">if set to <c>true</c> [is front].</param>
        /// <returns>A collection of records.</returns>
        protected override List<Record> GetImageRecords( ExportOptions options, FinancialTransaction transaction, FinancialTransactionImage image, bool isFront )
        {

            var institutionRoutingNumber = Rock.Security.Encryption.DecryptString( GetAttributeValue( options.FileFormat, "InstitutionRoutingNumber" ) );

            var records = base.GetImageRecords( options, transaction, image, isFront );

            var detail = records.Where( r => r.RecordType == 50 ).Cast<ImageViewDetail>().FirstOrDefault();
            if ( detail != null )
            {
                detail.ImageCreatorRoutingNumber = institutionRoutingNumber;
            }

            var data = records.Where( r => r.RecordType == 52 ).Cast<ImageViewData>().FirstOrDefault();
            if ( data != null )
            {
                data.InstitutionRoutingNumber = institutionRoutingNumber;
            }

            return records;
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
    }
}


