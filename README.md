# Scheduler
The Scheduler application is a program that will automatically process all requests received from the Brevium
API and schedule the ideal appointment for the patient based on the request. The application populates the initial
schedule once using the "GET" schedule endpoint provided by the Brevium API.

The solution is implemented through a Rest API endpoint that kicks off the process. Depending on the real world
application of this proof of concept, other options such as a nightly batch job might also be appropriate compared to an API.

## Features
 - Appointments may only be scheduled on the hour
 - Appointments may be scheduled from 8am UTC until 4pm UTC
 - Only weekdays from November 2021 - December 2021 are allowed
 - Appointments may be scheduled on Holidays
 - A doctor may only have one appointment per hour (but different doctors same time is okay)
 - Each appointment for a given patient must have at least one week of separation between their other appointments
 - New patients can only be scheduled for 3pm and 4pm

## Future Improvements
 - Simplify the process for making calls to the Brevium API (ie. don't use a separate function for each)
 - More error handling
 - Break down some methods into their smaller processes