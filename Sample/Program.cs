using Penneo;
using System;

namespace Sample
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("Penneo Testing!");
            PenneoConnector.Initialize("", "", "https://app.penneo.com/api/v1/");

            // Create a new case file
            var myCaseFile = new CaseFile("Demo case file");
            myCaseFile.Persist();

            // Create a new signable document in this case file
            var myDocument = new Document(myCaseFile, "Demo Document", @".\document.pdf");
            myDocument.MakeSignable();
            myDocument.Persist();

            for (int i = 0; i < 3; i++)
            {
                // Create a new signer that can sign documents in the case file
                var mySigner = new Signer(myCaseFile, "John Doe " + i);

                mySigner.Persist();

                // Create a new signature line on the document
                var mySignatureLine = new SignatureLine(myDocument, "MySignerRole " + i);

                mySignatureLine.Persist();

                // Map the signer to the signing request
                mySignatureLine.SetSigner(mySigner);

                // Update the signing request for the new signer
                var mySigningRequest = mySigner.GetSigningRequest();
                mySigningRequest.SuccessUrl = "http://localhost:64083/api/board/1a7911e4-feb5-4107-b64b-3d79b8e4c49a/signatures/success";
                mySigningRequest.FailUrl = "http://localhost:64083/api/board/1a7911e4-feb5-4107-b64b-3d79b8e4c49a/signatures/failure";
                mySigningRequest.Email = "thomas@board-governance.com";
                mySigningRequest.Persist();

                Console.WriteLine("<a href=\"" + mySigningRequest.GetLink() + "\">Sign now</a>");
            }

            // "Package" the case file for "sending".
            myCaseFile.Send();

            // And finally, print out the link leading to the signing portal.
            // The signer uses this link to sign the document.
            Console.WriteLine("Finished");
            Console.ReadLine();
        }
    }
}
