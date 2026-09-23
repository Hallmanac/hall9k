--
-- PostgreSQL database dump
--

\restrict Xa23F1ktberAR6lTCao9kkoHuZF5FXQWHKYTfN0LWGOwDZ3sntDN4gpCZ1Ug4Ks

-- Dumped from database version 18.3 (Debian 18.3-1.pgdg13+1)
-- Dumped by pg_dump version 18.6 (Homebrew)

SET statement_timeout = 0;
SET lock_timeout = 0;
SET idle_in_transaction_session_timeout = 0;
SET transaction_timeout = 0;
SET client_encoding = 'UTF8';
SET standard_conforming_strings = on;
SELECT pg_catalog.set_config('search_path', '', false);
SET check_function_bodies = false;
SET xmloption = content;
SET client_min_messages = warning;
SET row_security = off;

--
-- Name: mt_archive_stream(uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_archive_stream(streamid uuid) RETURNS void
    LANGUAGE plpgsql
    AS $$
BEGIN
  update public.mt_streams set is_archived = TRUE where id = streamid ;
  update public.mt_events set is_archived = TRUE where stream_id = streamid ;
END;
$$;


--
-- Name: mt_grams_array(text, boolean); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_grams_array(words text, use_unaccent boolean DEFAULT false) RETURNS text[]
    LANGUAGE plpgsql IMMUTABLE STRICT
    AS $$
        DECLARE
result text[];
        DECLARE
word text;
        DECLARE
clean_word text;
BEGIN
                FOREACH
word IN ARRAY string_to_array(words, ' ')
                LOOP
                     clean_word = regexp_replace(public.mt_safe_unaccent(use_unaccent, word), '[^a-zA-Z0-9]+', '','g');
FOR i IN 1 .. length(clean_word)
                     LOOP
                         result := result || quote_literal(substr(lower(clean_word), i, 1));
                         result
:= result || quote_literal(substr(lower(clean_word), i, 2));
                         result
:= result || quote_literal(substr(lower(clean_word), i, 3));
END LOOP;
END LOOP;

RETURN ARRAY(SELECT DISTINCT e FROM unnest(result) AS a(e) ORDER BY e);
END;
$$;


--
-- Name: mt_grams_query(text, boolean); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_grams_query(text, use_unaccent boolean DEFAULT false) RETURNS tsquery
    LANGUAGE plpgsql IMMUTABLE STRICT
    AS $_$
BEGIN
RETURN (SELECT array_to_string(public.mt_grams_array($1, use_unaccent), ' & ') ::tsquery);
END
$_$;


--
-- Name: mt_grams_vector(text, boolean); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_grams_vector(text, use_unaccent boolean DEFAULT false) RETURNS tsvector
    LANGUAGE plpgsql IMMUTABLE STRICT
    AS $_$
BEGIN
RETURN (SELECT array_to_string(public.mt_grams_array($1, use_unaccent), ' ') ::tsvector);
END
$_$;


--
-- Name: mt_immutable_date(text); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_immutable_date(value text) RETURNS date
    LANGUAGE sql IMMUTABLE
    AS $$
select value::date

$$;


--
-- Name: mt_immutable_time(text); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_immutable_time(value text) RETURNS time without time zone
    LANGUAGE sql IMMUTABLE
    AS $$
select value::time

$$;


--
-- Name: mt_immutable_timestamp(text); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_immutable_timestamp(value text) RETURNS timestamp without time zone
    LANGUAGE sql IMMUTABLE
    AS $$
select value::timestamp

$$;


--
-- Name: mt_immutable_timestamptz(text); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_immutable_timestamptz(value text) RETURNS timestamp with time zone
    LANGUAGE sql IMMUTABLE
    AS $$
select value::timestamptz

$$;


--
-- Name: mt_insert_autoprreviewdefaultadoption(jsonb, character varying, uuid, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_insert_autoprreviewdefaultadoption(doc jsonb, docdotnettype character varying, docid uuid, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
BEGIN
INSERT INTO public.mt_doc_autoprreviewdefaultadoption ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, docVersion, transaction_timestamp());

  RETURN docVersion;
END;
$$;


--
-- Name: mt_insert_cleanbasegateverdict(jsonb, character varying, character varying, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_insert_cleanbasegateverdict(doc jsonb, docdotnettype character varying, docid character varying, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
BEGIN
INSERT INTO public.mt_doc_cleanbasegateverdict ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, docVersion, transaction_timestamp());

  RETURN docVersion;
END;
$$;


--
-- Name: mt_insert_connectiondetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_insert_connectiondetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
BEGIN
INSERT INTO public.mt_doc_connectiondetails ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp());
  RETURN 1;
END;
$$;


--
-- Name: mt_insert_courierdayspawncounter(jsonb, character varying, uuid, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_insert_courierdayspawncounter(doc jsonb, docdotnettype character varying, docid uuid, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
BEGIN
INSERT INTO public.mt_doc_courierdayspawncounter ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, docVersion, transaction_timestamp());

  RETURN docVersion;
END;
$$;


--
-- Name: mt_insert_courierrundetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_insert_courierrundetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
BEGIN
INSERT INTO public.mt_doc_courierrundetails ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp());
  RETURN 1;
END;
$$;


--
-- Name: mt_insert_deadletterevent(jsonb, character varying, uuid, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_insert_deadletterevent(doc jsonb, docdotnettype character varying, docid uuid, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
BEGIN
INSERT INTO public.mt_doc_deadletterevent ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, docVersion, transaction_timestamp());

  RETURN docVersion;
END;
$$;


--
-- Name: mt_insert_decisiondetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_insert_decisiondetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
BEGIN
INSERT INTO public.mt_doc_decisiondetails ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp());
  RETURN 1;
END;
$$;


--
-- Name: mt_insert_epicdetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_insert_epicdetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
BEGIN
INSERT INTO public.mt_doc_epicdetails ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp());
  RETURN 1;
END;
$$;


--
-- Name: mt_insert_eventcatchupinboxcursor(jsonb, character varying, uuid, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_insert_eventcatchupinboxcursor(doc jsonb, docdotnettype character varying, docid uuid, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
BEGIN
INSERT INTO public.mt_doc_eventcatchupinboxcursor ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, docVersion, transaction_timestamp());

  RETURN docVersion;
END;
$$;


--
-- Name: mt_insert_eventcatchuprequest(jsonb, character varying, uuid, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_insert_eventcatchuprequest(doc jsonb, docdotnettype character varying, docid uuid, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
BEGIN
INSERT INTO public.mt_doc_eventcatchuprequest ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, docVersion, transaction_timestamp());

  RETURN docVersion;
END;
$$;


--
-- Name: mt_insert_eventoriginprogress(jsonb, character varying, uuid, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_insert_eventoriginprogress(doc jsonb, docdotnettype character varying, docid uuid, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
BEGIN
INSERT INTO public.mt_doc_eventoriginprogress ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, docVersion, transaction_timestamp());

  RETURN docVersion;
END;
$$;


--
-- Name: mt_insert_eventreplicationinboxcursor(jsonb, character varying, uuid, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_insert_eventreplicationinboxcursor(doc jsonb, docdotnettype character varying, docid uuid, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
BEGIN
INSERT INTO public.mt_doc_eventreplicationinboxcursor ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, docVersion, transaction_timestamp());

  RETURN docVersion;
END;
$$;


--
-- Name: mt_insert_eventreplicationoutboxposition(jsonb, character varying, uuid, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_insert_eventreplicationoutboxposition(doc jsonb, docdotnettype character varying, docid uuid, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
BEGIN
INSERT INTO public.mt_doc_eventreplicationoutboxposition ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, docVersion, transaction_timestamp());

  RETURN docVersion;
END;
$$;


--
-- Name: mt_insert_fleetprojectreconcile(jsonb, character varying, uuid, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_insert_fleetprojectreconcile(doc jsonb, docdotnettype character varying, docid uuid, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
BEGIN
INSERT INTO public.mt_doc_fleetprojectreconcile ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, docVersion, transaction_timestamp());

  RETURN docVersion;
END;
$$;


--
-- Name: mt_insert_heldreplicatedeventrecord(jsonb, character varying, uuid, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_insert_heldreplicatedeventrecord(doc jsonb, docdotnettype character varying, docid uuid, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
BEGIN
INSERT INTO public.mt_doc_heldreplicatedeventrecord ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, docVersion, transaction_timestamp());

  RETURN docVersion;
END;
$$;


--
-- Name: mt_insert_ideadetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_insert_ideadetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
BEGIN
INSERT INTO public.mt_doc_ideadetails ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp());
  RETURN 1;
END;
$$;


--
-- Name: mt_insert_invitedetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_insert_invitedetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
BEGIN
INSERT INTO public.mt_doc_invitedetails ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp());
  RETURN 1;
END;
$$;


--
-- Name: mt_insert_learningdetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_insert_learningdetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
BEGIN
INSERT INTO public.mt_doc_learningdetails ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp());
  RETURN 1;
END;
$$;


--
-- Name: mt_insert_legacymessageadoptiondetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_insert_legacymessageadoptiondetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
BEGIN
INSERT INTO public.mt_doc_legacymessageadoptiondetails ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp());
  RETURN 1;
END;
$$;


--
-- Name: mt_insert_messagedetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_insert_messagedetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
BEGIN
INSERT INTO public.mt_doc_messagedetails ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp());
  RETURN 1;
END;
$$;


--
-- Name: mt_insert_messageinboxdetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_insert_messageinboxdetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
BEGIN
INSERT INTO public.mt_doc_messageinboxdetails ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp());
  RETURN 1;
END;
$$;


--
-- Name: mt_insert_nodedetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_insert_nodedetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
BEGIN
INSERT INTO public.mt_doc_nodedetails ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp());
  RETURN 1;
END;
$$;


--
-- Name: mt_insert_nodedispatchload(jsonb, character varying, uuid, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_insert_nodedispatchload(doc jsonb, docdotnettype character varying, docid uuid, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
BEGIN
INSERT INTO public.mt_doc_nodedispatchload ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, docVersion, transaction_timestamp());

  RETURN docVersion;
END;
$$;


--
-- Name: mt_insert_observedreviewmention(jsonb, character varying, character varying, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_insert_observedreviewmention(doc jsonb, docdotnettype character varying, docid character varying, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
BEGIN
INSERT INTO public.mt_doc_observedreviewmention ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, docVersion, transaction_timestamp());

  RETURN docVersion;
END;
$$;


--
-- Name: mt_insert_observedreviewrequest(jsonb, character varying, character varying, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_insert_observedreviewrequest(doc jsonb, docdotnettype character varying, docid character varying, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
BEGIN
INSERT INTO public.mt_doc_observedreviewrequest ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, docVersion, transaction_timestamp());

  RETURN docVersion;
END;
$$;


--
-- Name: mt_insert_orchestratorfeedcursor(jsonb, character varying, uuid, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_insert_orchestratorfeedcursor(doc jsonb, docdotnettype character varying, docid uuid, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
BEGIN
INSERT INTO public.mt_doc_orchestratorfeedcursor ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, docVersion, transaction_timestamp());

  RETURN docVersion;
END;
$$;


--
-- Name: mt_insert_orchestratorfeeddrainlease(jsonb, character varying, uuid, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_insert_orchestratorfeeddrainlease(doc jsonb, docdotnettype character varying, docid uuid, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
BEGIN
INSERT INTO public.mt_doc_orchestratorfeeddrainlease ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, docVersion, transaction_timestamp());

  RETURN docVersion;
END;
$$;


--
-- Name: mt_insert_orchestratorpresencedetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_insert_orchestratorpresencedetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
BEGIN
INSERT INTO public.mt_doc_orchestratorpresencedetails ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp());
  RETURN 1;
END;
$$;


--
-- Name: mt_insert_ownerdetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_insert_ownerdetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
BEGIN
INSERT INTO public.mt_doc_ownerdetails ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp());
  RETURN 1;
END;
$$;


--
-- Name: mt_insert_projectdetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_insert_projectdetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
BEGIN
INSERT INTO public.mt_doc_projectdetails ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp());
  RETURN 1;
END;
$$;


--
-- Name: mt_insert_projectgithubmembers(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_insert_projectgithubmembers(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
BEGIN
INSERT INTO public.mt_doc_projectgithubmembers ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp());
  RETURN 1;
END;
$$;


--
-- Name: mt_insert_promptaddendasyncposition(jsonb, character varying, uuid, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_insert_promptaddendasyncposition(doc jsonb, docdotnettype character varying, docid uuid, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
BEGIN
INSERT INTO public.mt_doc_promptaddendasyncposition ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, docVersion, transaction_timestamp());

  RETURN docVersion;
END;
$$;


--
-- Name: mt_insert_purgedspendrecord(jsonb, character varying, uuid, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_insert_purgedspendrecord(doc jsonb, docdotnettype character varying, docid uuid, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
BEGIN
INSERT INTO public.mt_doc_purgedspendrecord ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, docVersion, transaction_timestamp());

  RETURN docVersion;
END;
$$;


--
-- Name: mt_insert_replicatedeventrecord(jsonb, character varying, uuid, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_insert_replicatedeventrecord(doc jsonb, docdotnettype character varying, docid uuid, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
BEGIN
INSERT INTO public.mt_doc_replicatedeventrecord ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, docVersion, transaction_timestamp());

  RETURN docVersion;
END;
$$;


--
-- Name: mt_insert_runactivity(jsonb, character varying, uuid, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_insert_runactivity(doc jsonb, docdotnettype character varying, docid uuid, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
BEGIN
INSERT INTO public.mt_doc_runactivity ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, docVersion, transaction_timestamp());

  RETURN docVersion;
END;
$$;


--
-- Name: mt_insert_rundetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_insert_rundetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
BEGIN
INSERT INTO public.mt_doc_rundetails ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp());
  RETURN 1;
END;
$$;


--
-- Name: mt_insert_runlistitem(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_insert_runlistitem(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
BEGIN
INSERT INTO public.mt_doc_runlistitem ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp());
  RETURN 1;
END;
$$;


--
-- Name: mt_insert_runskillsyncposition(jsonb, character varying, uuid, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_insert_runskillsyncposition(doc jsonb, docdotnettype character varying, docid uuid, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
BEGIN
INSERT INTO public.mt_doc_runskillsyncposition ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, docVersion, transaction_timestamp());

  RETURN docVersion;
END;
$$;


--
-- Name: mt_insert_taskdetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_insert_taskdetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
BEGIN
INSERT INTO public.mt_doc_taskdetails ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp());
  RETURN 1;
END;
$$;


--
-- Name: mt_insert_taskholderclaimhold(jsonb, character varying, character varying, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_insert_taskholderclaimhold(doc jsonb, docdotnettype character varying, docid character varying, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
BEGIN
INSERT INTO public.mt_doc_taskholderclaimhold ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, docVersion, transaction_timestamp());

  RETURN docVersion;
END;
$$;


--
-- Name: mt_insert_taskholderreleasepending(jsonb, character varying, character varying, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_insert_taskholderreleasepending(doc jsonb, docdotnettype character varying, docid character varying, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
BEGIN
INSERT INTO public.mt_doc_taskholderreleasepending ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, docVersion, transaction_timestamp());

  RETURN docVersion;
END;
$$;


--
-- Name: mt_insert_tasklease(jsonb, character varying, uuid, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_insert_tasklease(doc jsonb, docdotnettype character varying, docid uuid, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
BEGIN
INSERT INTO public.mt_doc_tasklease ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, docVersion, transaction_timestamp());

  RETURN docVersion;
END;
$$;


--
-- Name: mt_insert_tasklistitem(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_insert_tasklistitem(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
BEGIN
INSERT INTO public.mt_doc_tasklistitem ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp());
  RETURN 1;
END;
$$;


--
-- Name: mt_insert_tasktrackerassignmirrorpending(jsonb, character varying, character varying, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_insert_tasktrackerassignmirrorpending(doc jsonb, docdotnettype character varying, docid character varying, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
BEGIN
INSERT INTO public.mt_doc_tasktrackerassignmirrorpending ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, docVersion, transaction_timestamp());

  RETURN docVersion;
END;
$$;


--
-- Name: mt_insert_tasktrackerreleasemirrorpending(jsonb, character varying, character varying, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_insert_tasktrackerreleasemirrorpending(doc jsonb, docdotnettype character varying, docid character varying, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
BEGIN
INSERT INTO public.mt_doc_tasktrackerreleasemirrorpending ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, docVersion, transaction_timestamp());

  RETURN docVersion;
END;
$$;


--
-- Name: mt_insert_trackerclaimhold(jsonb, character varying, character varying, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_insert_trackerclaimhold(doc jsonb, docdotnettype character varying, docid character varying, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
BEGIN
INSERT INTO public.mt_doc_trackerclaimhold ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, docVersion, transaction_timestamp());

  RETURN docVersion;
END;
$$;


--
-- Name: mt_insert_unverifiedledgerwritedetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_insert_unverifiedledgerwritedetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
BEGIN
INSERT INTO public.mt_doc_unverifiedledgerwritedetails ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp());
  RETURN 1;
END;
$$;


--
-- Name: mt_jsonb_append(jsonb, text[], jsonb, boolean, jsonb); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_jsonb_append(jsonb, text[], jsonb, boolean, jsonb DEFAULT NULL::jsonb) RETURNS jsonb
    LANGUAGE plpgsql
    AS $_$
DECLARE
    retval ALIAS FOR $1;
    location ALIAS FOR $2;
    val ALIAS FOR $3;
    if_not_exists ALIAS FOR $4;
    patch_expression ALIAS FOR $5;
    tmp_value jsonb;
BEGIN
    tmp_value = retval #> location;
    IF tmp_value IS NOT NULL AND jsonb_typeof(tmp_value) = 'array' THEN
        CASE
            WHEN NOT if_not_exists THEN
                retval = jsonb_set(retval, location, tmp_value || val, FALSE);
            WHEN patch_expression IS NULL AND jsonb_typeof(val) = 'object' AND NOT tmp_value @> jsonb_build_array(val) THEN
                retval = jsonb_set(retval, location, tmp_value || val, FALSE);
            WHEN patch_expression IS NULL AND jsonb_typeof(val) <> 'object' AND NOT tmp_value @> val THEN
                retval = jsonb_set(retval, location, tmp_value || val, FALSE);
            WHEN patch_expression IS NOT NULL AND jsonb_typeof(patch_expression) = 'array' AND jsonb_array_length(patch_expression) = 0 THEN
                retval = jsonb_set(retval, location, tmp_value || val, FALSE);
            ELSE NULL;
            END CASE;
    END IF;
    RETURN retval;
END;
$_$;


--
-- Name: mt_jsonb_copy(jsonb, text[], text[]); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_jsonb_copy(jsonb, text[], text[]) RETURNS jsonb
    LANGUAGE plpgsql
    AS $_$
DECLARE
    retval ALIAS FOR $1;
    src_path ALIAS FOR $2;
    dst_path ALIAS FOR $3;
    tmp_value jsonb;
BEGIN
    tmp_value = retval #> src_path;
    retval = public.mt_jsonb_fix_null_parent(retval, dst_path);
    RETURN jsonb_set(retval, dst_path, tmp_value::jsonb, TRUE);
END;
$_$;


--
-- Name: mt_jsonb_duplicate(jsonb, text[], jsonb); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_jsonb_duplicate(jsonb, text[], jsonb) RETURNS jsonb
    LANGUAGE plpgsql
    AS $_$
DECLARE
    retval ALIAS FOR $1;
    location ALIAS FOR $2;
    targets ALIAS FOR $3;
    tmp_value jsonb;
    target_path text[];
    target text;
BEGIN
    FOR target IN SELECT jsonb_array_elements_text(targets)
    LOOP
        target_path = public.mt_jsonb_path_to_array(target, '\.');
        retval = public.mt_jsonb_copy(retval, location, target_path);
    END LOOP;

    RETURN retval;
END;
$_$;


--
-- Name: mt_jsonb_fix_null_parent(jsonb, text[]); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_jsonb_fix_null_parent(jsonb, text[]) RETURNS jsonb
    LANGUAGE plpgsql
    AS $_$
DECLARE
retval ALIAS FOR $1;
    dst_path ALIAS FOR $2;
    dst_path_segment text[] = ARRAY[]::text[];
    dst_path_array_length integer;
    i integer = 1;
BEGIN
    dst_path_array_length = array_length(dst_path, 1);
    WHILE i <=(dst_path_array_length - 1)
    LOOP
        dst_path_segment = dst_path_segment || ARRAY[dst_path[i]];
        IF retval #> dst_path_segment IS NULL OR retval #> dst_path_segment = 'null'::jsonb THEN
            retval = jsonb_set(retval, dst_path_segment, '{}'::jsonb, TRUE);
        END IF;
        i = i + 1;
    END LOOP;

    RETURN retval;
END;
$_$;


--
-- Name: mt_jsonb_increment(jsonb, text[], numeric); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_jsonb_increment(jsonb, text[], numeric) RETURNS jsonb
    LANGUAGE plpgsql
    AS $_$
DECLARE
retval ALIAS FOR $1;
    location ALIAS FOR $2;
    increment_value ALIAS FOR $3;
    tmp_value jsonb;
BEGIN
    tmp_value = retval #> location;
    IF tmp_value IS NULL THEN
        tmp_value = to_jsonb(0);
END IF;

RETURN jsonb_set(retval, location, to_jsonb(tmp_value::numeric + increment_value), TRUE);
END;
$_$;


--
-- Name: mt_jsonb_insert(jsonb, text[], jsonb, integer, boolean, jsonb); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_jsonb_insert(jsonb, text[], jsonb, integer, boolean, jsonb DEFAULT NULL::jsonb) RETURNS jsonb
    LANGUAGE plpgsql
    AS $_$
DECLARE
    retval ALIAS FOR $1;
    location ALIAS FOR $2;
    val ALIAS FOR $3;
    elm_index ALIAS FOR $4;
    if_not_exists ALIAS FOR $5;
    patch_expression ALIAS FOR $6;
    tmp_value jsonb;
BEGIN
    tmp_value = retval #> location;
    IF tmp_value IS NOT NULL AND jsonb_typeof(tmp_value) = 'array' THEN
        IF elm_index IS NULL THEN
            elm_index = jsonb_array_length(tmp_value) + 1;
        END IF;
        CASE
            WHEN NOT if_not_exists THEN
                retval = jsonb_insert(retval, location || elm_index::text, val);
            WHEN patch_expression IS NULL AND jsonb_typeof(val) = 'object' AND NOT tmp_value @> jsonb_build_array(val) THEN
                retval = jsonb_insert(retval, location || elm_index::text, val);
            WHEN patch_expression IS NULL AND jsonb_typeof(val) <> 'object' AND NOT tmp_value @> val THEN
                retval = jsonb_insert(retval, location || elm_index::text, val);
            WHEN patch_expression IS NOT NULL AND jsonb_typeof(patch_expression) = 'array' AND jsonb_array_length(patch_expression) = 0 THEN
                retval = jsonb_insert(retval, location || elm_index::text, val);
            ELSE NULL;
        END CASE;
    END IF;
    RETURN retval;
END;
$_$;


--
-- Name: mt_jsonb_move(jsonb, text[], text); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_jsonb_move(jsonb, text[], text) RETURNS jsonb
    LANGUAGE plpgsql
    AS $_$
DECLARE
    retval ALIAS FOR $1;
    src_path ALIAS FOR $2;
    dst_name ALIAS FOR $3;
    dst_path text[];
    tmp_value jsonb;
BEGIN
    tmp_value = retval #> src_path;
    retval = retval #- src_path;
    dst_path = src_path;
    dst_path[array_length(dst_path, 1)] = dst_name;
    retval = public.mt_jsonb_fix_null_parent(retval, dst_path);
    RETURN jsonb_set(retval, dst_path, tmp_value, TRUE);
END;
$_$;


--
-- Name: mt_jsonb_patch(jsonb, jsonb); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_jsonb_patch(jsonb, jsonb) RETURNS jsonb
    LANGUAGE plpgsql
    AS $_$
DECLARE
    retval ALIAS FOR $1;
    patchset ALIAS FOR $2;
    patch jsonb;
    patch_path text[];
    patch_expression jsonb;
    value jsonb;
BEGIN
    FOR patch IN SELECT * from jsonb_array_elements(patchset)
    LOOP
        patch_path = public.mt_jsonb_path_to_array((patch->>'path')::text, '\.');

        patch_expression = null;
        IF (patch->>'type') IN ('remove', 'append_if_not_exists', 'insert_if_not_exists') AND (patch->>'expression') IS NOT NULL THEN
            patch_expression = jsonb_path_query_array(retval #> patch_path, (patch->>'expression')::jsonpath);
        END IF;

        CASE patch->>'type'
            WHEN 'set' THEN
                retval = jsonb_set(retval, patch_path, (patch->'value')::jsonb, TRUE);
            WHEN 'delete' THEN
                retval = retval#-patch_path;
            WHEN 'append' THEN
                retval = public.mt_jsonb_append(retval, patch_path, (patch->'value')::jsonb, FALSE);
            WHEN 'append_if_not_exists' THEN
                retval = public.mt_jsonb_append(retval, patch_path, (patch->'value')::jsonb, TRUE, patch_expression);
            WHEN 'insert' THEN
                retval = public.mt_jsonb_insert(retval, patch_path, (patch->'value')::jsonb, (patch->>'index')::integer, FALSE);
            WHEN 'insert_if_not_exists' THEN
                retval = public.mt_jsonb_insert(retval, patch_path, (patch->'value')::jsonb, (patch->>'index')::integer, TRUE, patch_expression);
            WHEN 'remove' THEN
                retval = public.mt_jsonb_remove(retval, patch_path, COALESCE(patch_expression, (patch->'value')::jsonb));
            WHEN 'duplicate' THEN
                retval = public.mt_jsonb_duplicate(retval, patch_path, (patch->'targets')::jsonb);
            WHEN 'rename' THEN
                retval = public.mt_jsonb_move(retval, patch_path, (patch->>'to')::text);
            WHEN 'increment' THEN
                retval = public.mt_jsonb_increment(retval, patch_path, (patch->>'increment')::numeric);
            WHEN 'increment_float' THEN
                retval = public.mt_jsonb_increment(retval, patch_path, (patch->>'increment')::numeric);
            ELSE NULL;
        END CASE;
    END LOOP;
    RETURN retval;
END;
$_$;


--
-- Name: mt_jsonb_path_to_array(text, character); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_jsonb_path_to_array(text, character) RETURNS text[]
    LANGUAGE plpgsql
    AS $_$
DECLARE
    location ALIAS FOR $1;
    regex_pattern ALIAS FOR $2;
BEGIN
RETURN regexp_split_to_array(location, regex_pattern)::text[];
END;
$_$;


--
-- Name: mt_jsonb_remove(jsonb, text[], jsonb); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_jsonb_remove(jsonb, text[], jsonb) RETURNS jsonb
    LANGUAGE plpgsql
    AS $_$
DECLARE
    retval ALIAS FOR $1;
    location ALIAS FOR $2;
    val ALIAS FOR $3;
    tmp_value jsonb;
    tmp_remove jsonb;
    patch_remove jsonb;
BEGIN
    tmp_value = retval #> location;
    IF tmp_value IS NOT NULL AND jsonb_typeof(tmp_value) = 'array' THEN
        IF jsonb_typeof(val) = 'array' THEN
            tmp_remove = val;
        ELSE
            tmp_remove = jsonb_build_array(val);
        END IF;

        FOR patch_remove IN SELECT * FROM jsonb_array_elements(tmp_remove)
        LOOP
            tmp_value =(SELECT jsonb_agg(elem)
            FROM jsonb_array_elements(tmp_value) AS elem
            WHERE elem <> patch_remove);
        END LOOP;

        IF tmp_value IS NULL THEN
            tmp_value = '[]'::jsonb;
        END IF;
    END IF;
    RETURN jsonb_set(retval, location, tmp_value, FALSE);
END;
$_$;


--
-- Name: mt_mark_event_progression(character varying, bigint); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_mark_event_progression(name character varying, last_encountered bigint) RETURNS void
    LANGUAGE plpgsql
    AS $$
BEGIN
INSERT INTO public.mt_event_progression (name, last_seq_id, last_updated)
VALUES (name, last_encountered, transaction_timestamp())
ON CONFLICT ON CONSTRAINT pk_mt_event_progression
    DO
UPDATE SET last_seq_id = last_encountered, last_updated = transaction_timestamp();

END;

$$;


--
-- Name: mt_overwrite_connectiondetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_overwrite_connectiondetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN

  if revision = 0 then
    SELECT mt_version FROM public.mt_doc_connectiondetails into current_version WHERE id = docId ;
    if current_version is not null then
      revision = current_version + 1;
    else
      revision = 1;
    end if;
  end if;

  INSERT INTO public.mt_doc_connectiondetails ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp();

  SELECT mt_version FROM public.mt_doc_connectiondetails into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_overwrite_courierrundetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_overwrite_courierrundetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN

  if revision = 0 then
    SELECT mt_version FROM public.mt_doc_courierrundetails into current_version WHERE id = docId ;
    if current_version is not null then
      revision = current_version + 1;
    else
      revision = 1;
    end if;
  end if;

  INSERT INTO public.mt_doc_courierrundetails ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp();

  SELECT mt_version FROM public.mt_doc_courierrundetails into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_overwrite_decisiondetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_overwrite_decisiondetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN

  if revision = 0 then
    SELECT mt_version FROM public.mt_doc_decisiondetails into current_version WHERE id = docId ;
    if current_version is not null then
      revision = current_version + 1;
    else
      revision = 1;
    end if;
  end if;

  INSERT INTO public.mt_doc_decisiondetails ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp();

  SELECT mt_version FROM public.mt_doc_decisiondetails into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_overwrite_epicdetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_overwrite_epicdetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN

  if revision = 0 then
    SELECT mt_version FROM public.mt_doc_epicdetails into current_version WHERE id = docId ;
    if current_version is not null then
      revision = current_version + 1;
    else
      revision = 1;
    end if;
  end if;

  INSERT INTO public.mt_doc_epicdetails ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp();

  SELECT mt_version FROM public.mt_doc_epicdetails into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_overwrite_ideadetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_overwrite_ideadetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN

  if revision = 0 then
    SELECT mt_version FROM public.mt_doc_ideadetails into current_version WHERE id = docId ;
    if current_version is not null then
      revision = current_version + 1;
    else
      revision = 1;
    end if;
  end if;

  INSERT INTO public.mt_doc_ideadetails ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp();

  SELECT mt_version FROM public.mt_doc_ideadetails into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_overwrite_invitedetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_overwrite_invitedetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN

  if revision = 0 then
    SELECT mt_version FROM public.mt_doc_invitedetails into current_version WHERE id = docId ;
    if current_version is not null then
      revision = current_version + 1;
    else
      revision = 1;
    end if;
  end if;

  INSERT INTO public.mt_doc_invitedetails ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp();

  SELECT mt_version FROM public.mt_doc_invitedetails into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_overwrite_learningdetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_overwrite_learningdetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN

  if revision = 0 then
    SELECT mt_version FROM public.mt_doc_learningdetails into current_version WHERE id = docId ;
    if current_version is not null then
      revision = current_version + 1;
    else
      revision = 1;
    end if;
  end if;

  INSERT INTO public.mt_doc_learningdetails ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp();

  SELECT mt_version FROM public.mt_doc_learningdetails into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_overwrite_legacymessageadoptiondetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_overwrite_legacymessageadoptiondetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN

  if revision = 0 then
    SELECT mt_version FROM public.mt_doc_legacymessageadoptiondetails into current_version WHERE id = docId ;
    if current_version is not null then
      revision = current_version + 1;
    else
      revision = 1;
    end if;
  end if;

  INSERT INTO public.mt_doc_legacymessageadoptiondetails ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp();

  SELECT mt_version FROM public.mt_doc_legacymessageadoptiondetails into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_overwrite_messagedetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_overwrite_messagedetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN

  if revision = 0 then
    SELECT mt_version FROM public.mt_doc_messagedetails into current_version WHERE id = docId ;
    if current_version is not null then
      revision = current_version + 1;
    else
      revision = 1;
    end if;
  end if;

  INSERT INTO public.mt_doc_messagedetails ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp();

  SELECT mt_version FROM public.mt_doc_messagedetails into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_overwrite_messageinboxdetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_overwrite_messageinboxdetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN

  if revision = 0 then
    SELECT mt_version FROM public.mt_doc_messageinboxdetails into current_version WHERE id = docId ;
    if current_version is not null then
      revision = current_version + 1;
    else
      revision = 1;
    end if;
  end if;

  INSERT INTO public.mt_doc_messageinboxdetails ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp();

  SELECT mt_version FROM public.mt_doc_messageinboxdetails into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_overwrite_nodedetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_overwrite_nodedetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN

  if revision = 0 then
    SELECT mt_version FROM public.mt_doc_nodedetails into current_version WHERE id = docId ;
    if current_version is not null then
      revision = current_version + 1;
    else
      revision = 1;
    end if;
  end if;

  INSERT INTO public.mt_doc_nodedetails ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp();

  SELECT mt_version FROM public.mt_doc_nodedetails into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_overwrite_orchestratorpresencedetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_overwrite_orchestratorpresencedetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN

  if revision = 0 then
    SELECT mt_version FROM public.mt_doc_orchestratorpresencedetails into current_version WHERE id = docId ;
    if current_version is not null then
      revision = current_version + 1;
    else
      revision = 1;
    end if;
  end if;

  INSERT INTO public.mt_doc_orchestratorpresencedetails ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp();

  SELECT mt_version FROM public.mt_doc_orchestratorpresencedetails into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_overwrite_ownerdetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_overwrite_ownerdetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN

  if revision = 0 then
    SELECT mt_version FROM public.mt_doc_ownerdetails into current_version WHERE id = docId ;
    if current_version is not null then
      revision = current_version + 1;
    else
      revision = 1;
    end if;
  end if;

  INSERT INTO public.mt_doc_ownerdetails ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp();

  SELECT mt_version FROM public.mt_doc_ownerdetails into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_overwrite_projectdetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_overwrite_projectdetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN

  if revision = 0 then
    SELECT mt_version FROM public.mt_doc_projectdetails into current_version WHERE id = docId ;
    if current_version is not null then
      revision = current_version + 1;
    else
      revision = 1;
    end if;
  end if;

  INSERT INTO public.mt_doc_projectdetails ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp();

  SELECT mt_version FROM public.mt_doc_projectdetails into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_overwrite_projectgithubmembers(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_overwrite_projectgithubmembers(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN

  if revision = 0 then
    SELECT mt_version FROM public.mt_doc_projectgithubmembers into current_version WHERE id = docId ;
    if current_version is not null then
      revision = current_version + 1;
    else
      revision = 1;
    end if;
  end if;

  INSERT INTO public.mt_doc_projectgithubmembers ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp();

  SELECT mt_version FROM public.mt_doc_projectgithubmembers into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_overwrite_rundetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_overwrite_rundetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN

  if revision = 0 then
    SELECT mt_version FROM public.mt_doc_rundetails into current_version WHERE id = docId ;
    if current_version is not null then
      revision = current_version + 1;
    else
      revision = 1;
    end if;
  end if;

  INSERT INTO public.mt_doc_rundetails ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp();

  SELECT mt_version FROM public.mt_doc_rundetails into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_overwrite_runlistitem(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_overwrite_runlistitem(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN

  if revision = 0 then
    SELECT mt_version FROM public.mt_doc_runlistitem into current_version WHERE id = docId ;
    if current_version is not null then
      revision = current_version + 1;
    else
      revision = 1;
    end if;
  end if;

  INSERT INTO public.mt_doc_runlistitem ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp();

  SELECT mt_version FROM public.mt_doc_runlistitem into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_overwrite_taskdetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_overwrite_taskdetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN

  if revision = 0 then
    SELECT mt_version FROM public.mt_doc_taskdetails into current_version WHERE id = docId ;
    if current_version is not null then
      revision = current_version + 1;
    else
      revision = 1;
    end if;
  end if;

  INSERT INTO public.mt_doc_taskdetails ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp();

  SELECT mt_version FROM public.mt_doc_taskdetails into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_overwrite_tasklistitem(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_overwrite_tasklistitem(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN

  if revision = 0 then
    SELECT mt_version FROM public.mt_doc_tasklistitem into current_version WHERE id = docId ;
    if current_version is not null then
      revision = current_version + 1;
    else
      revision = 1;
    end if;
  end if;

  INSERT INTO public.mt_doc_tasklistitem ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp();

  SELECT mt_version FROM public.mt_doc_tasklistitem into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_overwrite_unverifiedledgerwritedetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_overwrite_unverifiedledgerwritedetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN

  if revision = 0 then
    SELECT mt_version FROM public.mt_doc_unverifiedledgerwritedetails into current_version WHERE id = docId ;
    if current_version is not null then
      revision = current_version + 1;
    else
      revision = 1;
    end if;
  end if;

  INSERT INTO public.mt_doc_unverifiedledgerwritedetails ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp();

  SELECT mt_version FROM public.mt_doc_unverifiedledgerwritedetails into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_quick_append_events(uuid, character varying, character varying, uuid[], character varying[], character varying[], jsonb[], jsonb[]); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_quick_append_events(stream uuid, stream_type character varying, tenantid character varying, event_ids uuid[], event_types character varying[], dotnet_types character varying[], bodies jsonb[], headers jsonb[]) RETURNS integer[]
    LANGUAGE plpgsql
    AS $$
DECLARE
	event_version int;
	event_type varchar;
	event_id uuid;
	body jsonb;
	index int;
	seq int;
    actual_tenant varchar;
	return_value int[];
BEGIN
	select version into event_version from public.mt_streams where id = stream;
	if event_version IS NULL then
		event_version = 0;
		insert into public.mt_streams (id, type, version, timestamp, tenant_id) values (stream, stream_type, 0, now(), tenantid);
    else
        if tenantid IS NOT NULL then
            select tenant_id into actual_tenant from public.mt_streams where id = stream;
            if actual_tenant != tenantid then
                RAISE EXCEPTION 'The tenantid does not match the existing stream';
            end if;
        end if;
	end if;

	index := 1;
	return_value := ARRAY[event_version + array_length(event_ids, 1)];

	foreach event_id in ARRAY event_ids
	loop
	    seq := nextval('public.mt_events_sequence');
		return_value := array_append(return_value, seq);

	    event_version := event_version + 1;
		event_type = event_types[index];
		body = bodies[index];

		insert into public.mt_events
			(seq_id, id, stream_id, version, data, type, tenant_id, timestamp, mt_dotnet_type, is_archived, headers)
		values
			(seq, event_id, stream, event_version, body, event_type, tenantid, (now() at time zone 'utc'), dotnet_types[index], FALSE, headers[index]);

		index := index + 1;
	end loop;

	update public.mt_streams set version = event_version, timestamp = now() where id = stream;

	return return_value;
END
$$;


--
-- Name: mt_safe_unaccent(boolean, text); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_safe_unaccent(use_unaccent boolean, word text) RETURNS text
    LANGUAGE plpgsql IMMUTABLE STRICT
    AS $$
BEGIN
IF use_unaccent THEN
    RETURN unaccent(word);
ELSE
    RETURN word;
END IF;
END;
$$;


--
-- Name: mt_update_autoprreviewdefaultadoption(jsonb, character varying, uuid, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_update_autoprreviewdefaultadoption(doc jsonb, docdotnettype character varying, docid uuid, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version uuid;
BEGIN
  UPDATE public.mt_doc_autoprreviewdefaultadoption SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = docVersion, mt_last_modified = transaction_timestamp() where id = docId;

  SELECT mt_version FROM public.mt_doc_autoprreviewdefaultadoption into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_update_cleanbasegateverdict(jsonb, character varying, character varying, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_update_cleanbasegateverdict(doc jsonb, docdotnettype character varying, docid character varying, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version uuid;
BEGIN
  UPDATE public.mt_doc_cleanbasegateverdict SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = docVersion, mt_last_modified = transaction_timestamp() where id = docId;

  SELECT mt_version FROM public.mt_doc_cleanbasegateverdict into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_update_connectiondetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_update_connectiondetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN
  if revision <= 1 then
    SELECT mt_version FROM public.mt_doc_connectiondetails into current_version WHERE id = docId ;
    if current_version is not null then
      revision = current_version + 1;
    end if;
  end if;

  UPDATE public.mt_doc_connectiondetails SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp() where revision > public.mt_doc_connectiondetails.mt_version and id = docId;

  SELECT mt_version FROM public.mt_doc_connectiondetails into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_update_courierdayspawncounter(jsonb, character varying, uuid, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_update_courierdayspawncounter(doc jsonb, docdotnettype character varying, docid uuid, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version uuid;
BEGIN
  UPDATE public.mt_doc_courierdayspawncounter SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = docVersion, mt_last_modified = transaction_timestamp() where id = docId;

  SELECT mt_version FROM public.mt_doc_courierdayspawncounter into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_update_courierrundetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_update_courierrundetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN
  if revision <= 1 then
    SELECT mt_version FROM public.mt_doc_courierrundetails into current_version WHERE id = docId ;
    if current_version is not null then
      revision = current_version + 1;
    end if;
  end if;

  UPDATE public.mt_doc_courierrundetails SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp() where revision > public.mt_doc_courierrundetails.mt_version and id = docId;

  SELECT mt_version FROM public.mt_doc_courierrundetails into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_update_deadletterevent(jsonb, character varying, uuid, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_update_deadletterevent(doc jsonb, docdotnettype character varying, docid uuid, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version uuid;
BEGIN
  UPDATE public.mt_doc_deadletterevent SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = docVersion, mt_last_modified = transaction_timestamp() where id = docId;

  SELECT mt_version FROM public.mt_doc_deadletterevent into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_update_decisiondetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_update_decisiondetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN
  if revision <= 1 then
    SELECT mt_version FROM public.mt_doc_decisiondetails into current_version WHERE id = docId ;
    if current_version is not null then
      revision = current_version + 1;
    end if;
  end if;

  UPDATE public.mt_doc_decisiondetails SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp() where revision > public.mt_doc_decisiondetails.mt_version and id = docId;

  SELECT mt_version FROM public.mt_doc_decisiondetails into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_update_epicdetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_update_epicdetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN
  if revision <= 1 then
    SELECT mt_version FROM public.mt_doc_epicdetails into current_version WHERE id = docId ;
    if current_version is not null then
      revision = current_version + 1;
    end if;
  end if;

  UPDATE public.mt_doc_epicdetails SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp() where revision > public.mt_doc_epicdetails.mt_version and id = docId;

  SELECT mt_version FROM public.mt_doc_epicdetails into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_update_eventcatchupinboxcursor(jsonb, character varying, uuid, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_update_eventcatchupinboxcursor(doc jsonb, docdotnettype character varying, docid uuid, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version uuid;
BEGIN
  UPDATE public.mt_doc_eventcatchupinboxcursor SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = docVersion, mt_last_modified = transaction_timestamp() where id = docId;

  SELECT mt_version FROM public.mt_doc_eventcatchupinboxcursor into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_update_eventcatchuprequest(jsonb, character varying, uuid, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_update_eventcatchuprequest(doc jsonb, docdotnettype character varying, docid uuid, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version uuid;
BEGIN
  UPDATE public.mt_doc_eventcatchuprequest SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = docVersion, mt_last_modified = transaction_timestamp() where id = docId;

  SELECT mt_version FROM public.mt_doc_eventcatchuprequest into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_update_eventoriginprogress(jsonb, character varying, uuid, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_update_eventoriginprogress(doc jsonb, docdotnettype character varying, docid uuid, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version uuid;
BEGIN
  UPDATE public.mt_doc_eventoriginprogress SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = docVersion, mt_last_modified = transaction_timestamp() where id = docId;

  SELECT mt_version FROM public.mt_doc_eventoriginprogress into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_update_eventreplicationinboxcursor(jsonb, character varying, uuid, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_update_eventreplicationinboxcursor(doc jsonb, docdotnettype character varying, docid uuid, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version uuid;
BEGIN
  UPDATE public.mt_doc_eventreplicationinboxcursor SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = docVersion, mt_last_modified = transaction_timestamp() where id = docId;

  SELECT mt_version FROM public.mt_doc_eventreplicationinboxcursor into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_update_eventreplicationoutboxposition(jsonb, character varying, uuid, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_update_eventreplicationoutboxposition(doc jsonb, docdotnettype character varying, docid uuid, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version uuid;
BEGIN
  UPDATE public.mt_doc_eventreplicationoutboxposition SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = docVersion, mt_last_modified = transaction_timestamp() where id = docId;

  SELECT mt_version FROM public.mt_doc_eventreplicationoutboxposition into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_update_fleetprojectreconcile(jsonb, character varying, uuid, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_update_fleetprojectreconcile(doc jsonb, docdotnettype character varying, docid uuid, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version uuid;
BEGIN
  UPDATE public.mt_doc_fleetprojectreconcile SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = docVersion, mt_last_modified = transaction_timestamp() where id = docId;

  SELECT mt_version FROM public.mt_doc_fleetprojectreconcile into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_update_heldreplicatedeventrecord(jsonb, character varying, uuid, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_update_heldreplicatedeventrecord(doc jsonb, docdotnettype character varying, docid uuid, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version uuid;
BEGIN
  UPDATE public.mt_doc_heldreplicatedeventrecord SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = docVersion, mt_last_modified = transaction_timestamp() where id = docId;

  SELECT mt_version FROM public.mt_doc_heldreplicatedeventrecord into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_update_ideadetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_update_ideadetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN
  if revision <= 1 then
    SELECT mt_version FROM public.mt_doc_ideadetails into current_version WHERE id = docId ;
    if current_version is not null then
      revision = current_version + 1;
    end if;
  end if;

  UPDATE public.mt_doc_ideadetails SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp() where revision > public.mt_doc_ideadetails.mt_version and id = docId;

  SELECT mt_version FROM public.mt_doc_ideadetails into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_update_invitedetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_update_invitedetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN
  if revision <= 1 then
    SELECT mt_version FROM public.mt_doc_invitedetails into current_version WHERE id = docId ;
    if current_version is not null then
      revision = current_version + 1;
    end if;
  end if;

  UPDATE public.mt_doc_invitedetails SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp() where revision > public.mt_doc_invitedetails.mt_version and id = docId;

  SELECT mt_version FROM public.mt_doc_invitedetails into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_update_learningdetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_update_learningdetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN
  if revision <= 1 then
    SELECT mt_version FROM public.mt_doc_learningdetails into current_version WHERE id = docId ;
    if current_version is not null then
      revision = current_version + 1;
    end if;
  end if;

  UPDATE public.mt_doc_learningdetails SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp() where revision > public.mt_doc_learningdetails.mt_version and id = docId;

  SELECT mt_version FROM public.mt_doc_learningdetails into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_update_legacymessageadoptiondetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_update_legacymessageadoptiondetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN
  if revision <= 1 then
    SELECT mt_version FROM public.mt_doc_legacymessageadoptiondetails into current_version WHERE id = docId ;
    if current_version is not null then
      revision = current_version + 1;
    end if;
  end if;

  UPDATE public.mt_doc_legacymessageadoptiondetails SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp() where revision > public.mt_doc_legacymessageadoptiondetails.mt_version and id = docId;

  SELECT mt_version FROM public.mt_doc_legacymessageadoptiondetails into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_update_messagedetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_update_messagedetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN
  if revision <= 1 then
    SELECT mt_version FROM public.mt_doc_messagedetails into current_version WHERE id = docId ;
    if current_version is not null then
      revision = current_version + 1;
    end if;
  end if;

  UPDATE public.mt_doc_messagedetails SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp() where revision > public.mt_doc_messagedetails.mt_version and id = docId;

  SELECT mt_version FROM public.mt_doc_messagedetails into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_update_messageinboxdetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_update_messageinboxdetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN
  if revision <= 1 then
    SELECT mt_version FROM public.mt_doc_messageinboxdetails into current_version WHERE id = docId ;
    if current_version is not null then
      revision = current_version + 1;
    end if;
  end if;

  UPDATE public.mt_doc_messageinboxdetails SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp() where revision > public.mt_doc_messageinboxdetails.mt_version and id = docId;

  SELECT mt_version FROM public.mt_doc_messageinboxdetails into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_update_nodedetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_update_nodedetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN
  if revision <= 1 then
    SELECT mt_version FROM public.mt_doc_nodedetails into current_version WHERE id = docId ;
    if current_version is not null then
      revision = current_version + 1;
    end if;
  end if;

  UPDATE public.mt_doc_nodedetails SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp() where revision > public.mt_doc_nodedetails.mt_version and id = docId;

  SELECT mt_version FROM public.mt_doc_nodedetails into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_update_nodedispatchload(jsonb, character varying, uuid, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_update_nodedispatchload(doc jsonb, docdotnettype character varying, docid uuid, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version uuid;
BEGIN
  UPDATE public.mt_doc_nodedispatchload SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = docVersion, mt_last_modified = transaction_timestamp() where id = docId;

  SELECT mt_version FROM public.mt_doc_nodedispatchload into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_update_observedreviewmention(jsonb, character varying, character varying, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_update_observedreviewmention(doc jsonb, docdotnettype character varying, docid character varying, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version uuid;
BEGIN
  UPDATE public.mt_doc_observedreviewmention SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = docVersion, mt_last_modified = transaction_timestamp() where id = docId;

  SELECT mt_version FROM public.mt_doc_observedreviewmention into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_update_observedreviewrequest(jsonb, character varying, character varying, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_update_observedreviewrequest(doc jsonb, docdotnettype character varying, docid character varying, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version uuid;
BEGIN
  UPDATE public.mt_doc_observedreviewrequest SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = docVersion, mt_last_modified = transaction_timestamp() where id = docId;

  SELECT mt_version FROM public.mt_doc_observedreviewrequest into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_update_orchestratorfeedcursor(jsonb, character varying, uuid, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_update_orchestratorfeedcursor(doc jsonb, docdotnettype character varying, docid uuid, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version uuid;
BEGIN
  UPDATE public.mt_doc_orchestratorfeedcursor SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = docVersion, mt_last_modified = transaction_timestamp() where id = docId;

  SELECT mt_version FROM public.mt_doc_orchestratorfeedcursor into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_update_orchestratorfeeddrainlease(jsonb, character varying, uuid, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_update_orchestratorfeeddrainlease(doc jsonb, docdotnettype character varying, docid uuid, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version uuid;
BEGIN
  UPDATE public.mt_doc_orchestratorfeeddrainlease SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = docVersion, mt_last_modified = transaction_timestamp() where id = docId;

  SELECT mt_version FROM public.mt_doc_orchestratorfeeddrainlease into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_update_orchestratorpresencedetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_update_orchestratorpresencedetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN
  if revision <= 1 then
    SELECT mt_version FROM public.mt_doc_orchestratorpresencedetails into current_version WHERE id = docId ;
    if current_version is not null then
      revision = current_version + 1;
    end if;
  end if;

  UPDATE public.mt_doc_orchestratorpresencedetails SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp() where revision > public.mt_doc_orchestratorpresencedetails.mt_version and id = docId;

  SELECT mt_version FROM public.mt_doc_orchestratorpresencedetails into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_update_ownerdetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_update_ownerdetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN
  if revision <= 1 then
    SELECT mt_version FROM public.mt_doc_ownerdetails into current_version WHERE id = docId ;
    if current_version is not null then
      revision = current_version + 1;
    end if;
  end if;

  UPDATE public.mt_doc_ownerdetails SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp() where revision > public.mt_doc_ownerdetails.mt_version and id = docId;

  SELECT mt_version FROM public.mt_doc_ownerdetails into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_update_projectdetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_update_projectdetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN
  if revision <= 1 then
    SELECT mt_version FROM public.mt_doc_projectdetails into current_version WHERE id = docId ;
    if current_version is not null then
      revision = current_version + 1;
    end if;
  end if;

  UPDATE public.mt_doc_projectdetails SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp() where revision > public.mt_doc_projectdetails.mt_version and id = docId;

  SELECT mt_version FROM public.mt_doc_projectdetails into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_update_projectgithubmembers(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_update_projectgithubmembers(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN
  if revision <= 1 then
    SELECT mt_version FROM public.mt_doc_projectgithubmembers into current_version WHERE id = docId ;
    if current_version is not null then
      revision = current_version + 1;
    end if;
  end if;

  UPDATE public.mt_doc_projectgithubmembers SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp() where revision > public.mt_doc_projectgithubmembers.mt_version and id = docId;

  SELECT mt_version FROM public.mt_doc_projectgithubmembers into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_update_promptaddendasyncposition(jsonb, character varying, uuid, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_update_promptaddendasyncposition(doc jsonb, docdotnettype character varying, docid uuid, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version uuid;
BEGIN
  UPDATE public.mt_doc_promptaddendasyncposition SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = docVersion, mt_last_modified = transaction_timestamp() where id = docId;

  SELECT mt_version FROM public.mt_doc_promptaddendasyncposition into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_update_purgedspendrecord(jsonb, character varying, uuid, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_update_purgedspendrecord(doc jsonb, docdotnettype character varying, docid uuid, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version uuid;
BEGIN
  UPDATE public.mt_doc_purgedspendrecord SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = docVersion, mt_last_modified = transaction_timestamp() where id = docId;

  SELECT mt_version FROM public.mt_doc_purgedspendrecord into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_update_replicatedeventrecord(jsonb, character varying, uuid, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_update_replicatedeventrecord(doc jsonb, docdotnettype character varying, docid uuid, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version uuid;
BEGIN
  UPDATE public.mt_doc_replicatedeventrecord SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = docVersion, mt_last_modified = transaction_timestamp() where id = docId;

  SELECT mt_version FROM public.mt_doc_replicatedeventrecord into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_update_runactivity(jsonb, character varying, uuid, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_update_runactivity(doc jsonb, docdotnettype character varying, docid uuid, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version uuid;
BEGIN
  UPDATE public.mt_doc_runactivity SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = docVersion, mt_last_modified = transaction_timestamp() where id = docId;

  SELECT mt_version FROM public.mt_doc_runactivity into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_update_rundetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_update_rundetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN
  if revision <= 1 then
    SELECT mt_version FROM public.mt_doc_rundetails into current_version WHERE id = docId ;
    if current_version is not null then
      revision = current_version + 1;
    end if;
  end if;

  UPDATE public.mt_doc_rundetails SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp() where revision > public.mt_doc_rundetails.mt_version and id = docId;

  SELECT mt_version FROM public.mt_doc_rundetails into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_update_runlistitem(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_update_runlistitem(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN
  if revision <= 1 then
    SELECT mt_version FROM public.mt_doc_runlistitem into current_version WHERE id = docId ;
    if current_version is not null then
      revision = current_version + 1;
    end if;
  end if;

  UPDATE public.mt_doc_runlistitem SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp() where revision > public.mt_doc_runlistitem.mt_version and id = docId;

  SELECT mt_version FROM public.mt_doc_runlistitem into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_update_runskillsyncposition(jsonb, character varying, uuid, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_update_runskillsyncposition(doc jsonb, docdotnettype character varying, docid uuid, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version uuid;
BEGIN
  UPDATE public.mt_doc_runskillsyncposition SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = docVersion, mt_last_modified = transaction_timestamp() where id = docId;

  SELECT mt_version FROM public.mt_doc_runskillsyncposition into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_update_taskdetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_update_taskdetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN
  if revision <= 1 then
    SELECT mt_version FROM public.mt_doc_taskdetails into current_version WHERE id = docId ;
    if current_version is not null then
      revision = current_version + 1;
    end if;
  end if;

  UPDATE public.mt_doc_taskdetails SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp() where revision > public.mt_doc_taskdetails.mt_version and id = docId;

  SELECT mt_version FROM public.mt_doc_taskdetails into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_update_taskholderclaimhold(jsonb, character varying, character varying, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_update_taskholderclaimhold(doc jsonb, docdotnettype character varying, docid character varying, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version uuid;
BEGIN
  UPDATE public.mt_doc_taskholderclaimhold SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = docVersion, mt_last_modified = transaction_timestamp() where id = docId;

  SELECT mt_version FROM public.mt_doc_taskholderclaimhold into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_update_taskholderreleasepending(jsonb, character varying, character varying, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_update_taskholderreleasepending(doc jsonb, docdotnettype character varying, docid character varying, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version uuid;
BEGIN
  UPDATE public.mt_doc_taskholderreleasepending SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = docVersion, mt_last_modified = transaction_timestamp() where id = docId;

  SELECT mt_version FROM public.mt_doc_taskholderreleasepending into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_update_tasklease(jsonb, character varying, uuid, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_update_tasklease(doc jsonb, docdotnettype character varying, docid uuid, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version uuid;
BEGIN
  UPDATE public.mt_doc_tasklease SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = docVersion, mt_last_modified = transaction_timestamp() where id = docId;

  SELECT mt_version FROM public.mt_doc_tasklease into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_update_tasklistitem(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_update_tasklistitem(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN
  if revision <= 1 then
    SELECT mt_version FROM public.mt_doc_tasklistitem into current_version WHERE id = docId ;
    if current_version is not null then
      revision = current_version + 1;
    end if;
  end if;

  UPDATE public.mt_doc_tasklistitem SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp() where revision > public.mt_doc_tasklistitem.mt_version and id = docId;

  SELECT mt_version FROM public.mt_doc_tasklistitem into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_update_tasktrackerassignmirrorpending(jsonb, character varying, character varying, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_update_tasktrackerassignmirrorpending(doc jsonb, docdotnettype character varying, docid character varying, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version uuid;
BEGIN
  UPDATE public.mt_doc_tasktrackerassignmirrorpending SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = docVersion, mt_last_modified = transaction_timestamp() where id = docId;

  SELECT mt_version FROM public.mt_doc_tasktrackerassignmirrorpending into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_update_tasktrackerreleasemirrorpending(jsonb, character varying, character varying, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_update_tasktrackerreleasemirrorpending(doc jsonb, docdotnettype character varying, docid character varying, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version uuid;
BEGIN
  UPDATE public.mt_doc_tasktrackerreleasemirrorpending SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = docVersion, mt_last_modified = transaction_timestamp() where id = docId;

  SELECT mt_version FROM public.mt_doc_tasktrackerreleasemirrorpending into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_update_trackerclaimhold(jsonb, character varying, character varying, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_update_trackerclaimhold(doc jsonb, docdotnettype character varying, docid character varying, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version uuid;
BEGIN
  UPDATE public.mt_doc_trackerclaimhold SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = docVersion, mt_last_modified = transaction_timestamp() where id = docId;

  SELECT mt_version FROM public.mt_doc_trackerclaimhold into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_update_unverifiedledgerwritedetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_update_unverifiedledgerwritedetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN
  if revision <= 1 then
    SELECT mt_version FROM public.mt_doc_unverifiedledgerwritedetails into current_version WHERE id = docId ;
    if current_version is not null then
      revision = current_version + 1;
    end if;
  end if;

  UPDATE public.mt_doc_unverifiedledgerwritedetails SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp() where revision > public.mt_doc_unverifiedledgerwritedetails.mt_version and id = docId;

  SELECT mt_version FROM public.mt_doc_unverifiedledgerwritedetails into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_upsert_autoprreviewdefaultadoption(jsonb, character varying, uuid, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_upsert_autoprreviewdefaultadoption(doc jsonb, docdotnettype character varying, docid uuid, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version uuid;
BEGIN
INSERT INTO public.mt_doc_autoprreviewdefaultadoption ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, docVersion, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = docVersion, mt_last_modified = transaction_timestamp();

  SELECT mt_version FROM public.mt_doc_autoprreviewdefaultadoption into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_upsert_cleanbasegateverdict(jsonb, character varying, character varying, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_upsert_cleanbasegateverdict(doc jsonb, docdotnettype character varying, docid character varying, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version uuid;
BEGIN
INSERT INTO public.mt_doc_cleanbasegateverdict ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, docVersion, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = docVersion, mt_last_modified = transaction_timestamp();

  SELECT mt_version FROM public.mt_doc_cleanbasegateverdict into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_upsert_connectiondetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_upsert_connectiondetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN

SELECT version into current_version FROM public.mt_streams WHERE id = docId ;
if revision = 0 then
  if current_version is not null then
    revision = current_version;
  else
    revision = 1;
  end if;
else
  if current_version is not null then
    if current_version > revision then
      return 0;
    end if;
  end if;
end if;

INSERT INTO public.mt_doc_connectiondetails ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp() where revision > public.mt_doc_connectiondetails.mt_version;

  SELECT mt_version into final_version FROM public.mt_doc_connectiondetails WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_upsert_courierdayspawncounter(jsonb, character varying, uuid, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_upsert_courierdayspawncounter(doc jsonb, docdotnettype character varying, docid uuid, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version uuid;
BEGIN
INSERT INTO public.mt_doc_courierdayspawncounter ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, docVersion, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = docVersion, mt_last_modified = transaction_timestamp();

  SELECT mt_version FROM public.mt_doc_courierdayspawncounter into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_upsert_courierrundetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_upsert_courierrundetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN

SELECT version into current_version FROM public.mt_streams WHERE id = docId ;
if revision = 0 then
  if current_version is not null then
    revision = current_version;
  else
    revision = 1;
  end if;
else
  if current_version is not null then
    if current_version > revision then
      return 0;
    end if;
  end if;
end if;

INSERT INTO public.mt_doc_courierrundetails ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp() where revision > public.mt_doc_courierrundetails.mt_version;

  SELECT mt_version into final_version FROM public.mt_doc_courierrundetails WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_upsert_deadletterevent(jsonb, character varying, uuid, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_upsert_deadletterevent(doc jsonb, docdotnettype character varying, docid uuid, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version uuid;
BEGIN
INSERT INTO public.mt_doc_deadletterevent ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, docVersion, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = docVersion, mt_last_modified = transaction_timestamp();

  SELECT mt_version FROM public.mt_doc_deadletterevent into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_upsert_decisiondetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_upsert_decisiondetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN

SELECT version into current_version FROM public.mt_streams WHERE id = docId ;
if revision = 0 then
  if current_version is not null then
    revision = current_version;
  else
    revision = 1;
  end if;
else
  if current_version is not null then
    if current_version > revision then
      return 0;
    end if;
  end if;
end if;

INSERT INTO public.mt_doc_decisiondetails ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp() where revision > public.mt_doc_decisiondetails.mt_version;

  SELECT mt_version into final_version FROM public.mt_doc_decisiondetails WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_upsert_epicdetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_upsert_epicdetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN

SELECT version into current_version FROM public.mt_streams WHERE id = docId ;
if revision = 0 then
  if current_version is not null then
    revision = current_version;
  else
    revision = 1;
  end if;
else
  if current_version is not null then
    if current_version > revision then
      return 0;
    end if;
  end if;
end if;

INSERT INTO public.mt_doc_epicdetails ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp() where revision > public.mt_doc_epicdetails.mt_version;

  SELECT mt_version into final_version FROM public.mt_doc_epicdetails WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_upsert_eventcatchupinboxcursor(jsonb, character varying, uuid, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_upsert_eventcatchupinboxcursor(doc jsonb, docdotnettype character varying, docid uuid, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version uuid;
BEGIN
INSERT INTO public.mt_doc_eventcatchupinboxcursor ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, docVersion, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = docVersion, mt_last_modified = transaction_timestamp();

  SELECT mt_version FROM public.mt_doc_eventcatchupinboxcursor into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_upsert_eventcatchuprequest(jsonb, character varying, uuid, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_upsert_eventcatchuprequest(doc jsonb, docdotnettype character varying, docid uuid, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version uuid;
BEGIN
INSERT INTO public.mt_doc_eventcatchuprequest ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, docVersion, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = docVersion, mt_last_modified = transaction_timestamp();

  SELECT mt_version FROM public.mt_doc_eventcatchuprequest into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_upsert_eventoriginprogress(jsonb, character varying, uuid, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_upsert_eventoriginprogress(doc jsonb, docdotnettype character varying, docid uuid, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version uuid;
BEGIN
INSERT INTO public.mt_doc_eventoriginprogress ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, docVersion, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = docVersion, mt_last_modified = transaction_timestamp();

  SELECT mt_version FROM public.mt_doc_eventoriginprogress into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_upsert_eventreplicationinboxcursor(jsonb, character varying, uuid, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_upsert_eventreplicationinboxcursor(doc jsonb, docdotnettype character varying, docid uuid, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version uuid;
BEGIN
INSERT INTO public.mt_doc_eventreplicationinboxcursor ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, docVersion, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = docVersion, mt_last_modified = transaction_timestamp();

  SELECT mt_version FROM public.mt_doc_eventreplicationinboxcursor into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_upsert_eventreplicationoutboxposition(jsonb, character varying, uuid, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_upsert_eventreplicationoutboxposition(doc jsonb, docdotnettype character varying, docid uuid, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version uuid;
BEGIN
INSERT INTO public.mt_doc_eventreplicationoutboxposition ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, docVersion, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = docVersion, mt_last_modified = transaction_timestamp();

  SELECT mt_version FROM public.mt_doc_eventreplicationoutboxposition into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_upsert_fleetprojectreconcile(jsonb, character varying, uuid, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_upsert_fleetprojectreconcile(doc jsonb, docdotnettype character varying, docid uuid, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version uuid;
BEGIN
INSERT INTO public.mt_doc_fleetprojectreconcile ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, docVersion, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = docVersion, mt_last_modified = transaction_timestamp();

  SELECT mt_version FROM public.mt_doc_fleetprojectreconcile into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_upsert_heldreplicatedeventrecord(jsonb, character varying, uuid, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_upsert_heldreplicatedeventrecord(doc jsonb, docdotnettype character varying, docid uuid, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version uuid;
BEGIN
INSERT INTO public.mt_doc_heldreplicatedeventrecord ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, docVersion, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = docVersion, mt_last_modified = transaction_timestamp();

  SELECT mt_version FROM public.mt_doc_heldreplicatedeventrecord into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_upsert_ideadetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_upsert_ideadetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN

SELECT version into current_version FROM public.mt_streams WHERE id = docId ;
if revision = 0 then
  if current_version is not null then
    revision = current_version;
  else
    revision = 1;
  end if;
else
  if current_version is not null then
    if current_version > revision then
      return 0;
    end if;
  end if;
end if;

INSERT INTO public.mt_doc_ideadetails ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp() where revision > public.mt_doc_ideadetails.mt_version;

  SELECT mt_version into final_version FROM public.mt_doc_ideadetails WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_upsert_invitedetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_upsert_invitedetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN

SELECT version into current_version FROM public.mt_streams WHERE id = docId ;
if revision = 0 then
  if current_version is not null then
    revision = current_version;
  else
    revision = 1;
  end if;
else
  if current_version is not null then
    if current_version > revision then
      return 0;
    end if;
  end if;
end if;

INSERT INTO public.mt_doc_invitedetails ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp() where revision > public.mt_doc_invitedetails.mt_version;

  SELECT mt_version into final_version FROM public.mt_doc_invitedetails WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_upsert_learningdetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_upsert_learningdetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN

SELECT version into current_version FROM public.mt_streams WHERE id = docId ;
if revision = 0 then
  if current_version is not null then
    revision = current_version;
  else
    revision = 1;
  end if;
else
  if current_version is not null then
    if current_version > revision then
      return 0;
    end if;
  end if;
end if;

INSERT INTO public.mt_doc_learningdetails ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp() where revision > public.mt_doc_learningdetails.mt_version;

  SELECT mt_version into final_version FROM public.mt_doc_learningdetails WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_upsert_legacymessageadoptiondetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_upsert_legacymessageadoptiondetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN

SELECT version into current_version FROM public.mt_streams WHERE id = docId ;
if revision = 0 then
  if current_version is not null then
    revision = current_version;
  else
    revision = 1;
  end if;
else
  if current_version is not null then
    if current_version > revision then
      return 0;
    end if;
  end if;
end if;

INSERT INTO public.mt_doc_legacymessageadoptiondetails ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp() where revision > public.mt_doc_legacymessageadoptiondetails.mt_version;

  SELECT mt_version into final_version FROM public.mt_doc_legacymessageadoptiondetails WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_upsert_messagedetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_upsert_messagedetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN

SELECT version into current_version FROM public.mt_streams WHERE id = docId ;
if revision = 0 then
  if current_version is not null then
    revision = current_version;
  else
    revision = 1;
  end if;
else
  if current_version is not null then
    if current_version > revision then
      return 0;
    end if;
  end if;
end if;

INSERT INTO public.mt_doc_messagedetails ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp() where revision > public.mt_doc_messagedetails.mt_version;

  SELECT mt_version into final_version FROM public.mt_doc_messagedetails WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_upsert_messageinboxdetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_upsert_messageinboxdetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN

SELECT version into current_version FROM public.mt_streams WHERE id = docId ;
if revision = 0 then
  if current_version is not null then
    revision = current_version;
  else
    revision = 1;
  end if;
else
  if current_version is not null then
    if current_version > revision then
      return 0;
    end if;
  end if;
end if;

INSERT INTO public.mt_doc_messageinboxdetails ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp() where revision > public.mt_doc_messageinboxdetails.mt_version;

  SELECT mt_version into final_version FROM public.mt_doc_messageinboxdetails WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_upsert_nodedetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_upsert_nodedetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN

SELECT version into current_version FROM public.mt_streams WHERE id = docId ;
if revision = 0 then
  if current_version is not null then
    revision = current_version;
  else
    revision = 1;
  end if;
else
  if current_version is not null then
    if current_version > revision then
      return 0;
    end if;
  end if;
end if;

INSERT INTO public.mt_doc_nodedetails ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp() where revision > public.mt_doc_nodedetails.mt_version;

  SELECT mt_version into final_version FROM public.mt_doc_nodedetails WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_upsert_nodedispatchload(jsonb, character varying, uuid, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_upsert_nodedispatchload(doc jsonb, docdotnettype character varying, docid uuid, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version uuid;
BEGIN
INSERT INTO public.mt_doc_nodedispatchload ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, docVersion, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = docVersion, mt_last_modified = transaction_timestamp();

  SELECT mt_version FROM public.mt_doc_nodedispatchload into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_upsert_observedreviewmention(jsonb, character varying, character varying, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_upsert_observedreviewmention(doc jsonb, docdotnettype character varying, docid character varying, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version uuid;
BEGIN
INSERT INTO public.mt_doc_observedreviewmention ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, docVersion, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = docVersion, mt_last_modified = transaction_timestamp();

  SELECT mt_version FROM public.mt_doc_observedreviewmention into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_upsert_observedreviewrequest(jsonb, character varying, character varying, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_upsert_observedreviewrequest(doc jsonb, docdotnettype character varying, docid character varying, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version uuid;
BEGIN
INSERT INTO public.mt_doc_observedreviewrequest ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, docVersion, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = docVersion, mt_last_modified = transaction_timestamp();

  SELECT mt_version FROM public.mt_doc_observedreviewrequest into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_upsert_orchestratorfeedcursor(jsonb, character varying, uuid, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_upsert_orchestratorfeedcursor(doc jsonb, docdotnettype character varying, docid uuid, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version uuid;
BEGIN
INSERT INTO public.mt_doc_orchestratorfeedcursor ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, docVersion, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = docVersion, mt_last_modified = transaction_timestamp();

  SELECT mt_version FROM public.mt_doc_orchestratorfeedcursor into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_upsert_orchestratorfeeddrainlease(jsonb, character varying, uuid, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_upsert_orchestratorfeeddrainlease(doc jsonb, docdotnettype character varying, docid uuid, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version uuid;
BEGIN
INSERT INTO public.mt_doc_orchestratorfeeddrainlease ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, docVersion, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = docVersion, mt_last_modified = transaction_timestamp();

  SELECT mt_version FROM public.mt_doc_orchestratorfeeddrainlease into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_upsert_orchestratorpresencedetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_upsert_orchestratorpresencedetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN

SELECT version into current_version FROM public.mt_streams WHERE id = docId ;
if revision = 0 then
  if current_version is not null then
    revision = current_version;
  else
    revision = 1;
  end if;
else
  if current_version is not null then
    if current_version > revision then
      return 0;
    end if;
  end if;
end if;

INSERT INTO public.mt_doc_orchestratorpresencedetails ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp() where revision > public.mt_doc_orchestratorpresencedetails.mt_version;

  SELECT mt_version into final_version FROM public.mt_doc_orchestratorpresencedetails WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_upsert_ownerdetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_upsert_ownerdetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN

SELECT version into current_version FROM public.mt_streams WHERE id = docId ;
if revision = 0 then
  if current_version is not null then
    revision = current_version;
  else
    revision = 1;
  end if;
else
  if current_version is not null then
    if current_version > revision then
      return 0;
    end if;
  end if;
end if;

INSERT INTO public.mt_doc_ownerdetails ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp() where revision > public.mt_doc_ownerdetails.mt_version;

  SELECT mt_version into final_version FROM public.mt_doc_ownerdetails WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_upsert_projectdetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_upsert_projectdetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN

SELECT version into current_version FROM public.mt_streams WHERE id = docId ;
if revision = 0 then
  if current_version is not null then
    revision = current_version;
  else
    revision = 1;
  end if;
else
  if current_version is not null then
    if current_version > revision then
      return 0;
    end if;
  end if;
end if;

INSERT INTO public.mt_doc_projectdetails ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp() where revision > public.mt_doc_projectdetails.mt_version;

  SELECT mt_version into final_version FROM public.mt_doc_projectdetails WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_upsert_projectgithubmembers(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_upsert_projectgithubmembers(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN

SELECT version into current_version FROM public.mt_streams WHERE id = docId ;
if revision = 0 then
  if current_version is not null then
    revision = current_version;
  else
    revision = 1;
  end if;
else
  if current_version is not null then
    if current_version > revision then
      return 0;
    end if;
  end if;
end if;

INSERT INTO public.mt_doc_projectgithubmembers ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp() where revision > public.mt_doc_projectgithubmembers.mt_version;

  SELECT mt_version into final_version FROM public.mt_doc_projectgithubmembers WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_upsert_promptaddendasyncposition(jsonb, character varying, uuid, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_upsert_promptaddendasyncposition(doc jsonb, docdotnettype character varying, docid uuid, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version uuid;
BEGIN
INSERT INTO public.mt_doc_promptaddendasyncposition ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, docVersion, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = docVersion, mt_last_modified = transaction_timestamp();

  SELECT mt_version FROM public.mt_doc_promptaddendasyncposition into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_upsert_purgedspendrecord(jsonb, character varying, uuid, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_upsert_purgedspendrecord(doc jsonb, docdotnettype character varying, docid uuid, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version uuid;
BEGIN
INSERT INTO public.mt_doc_purgedspendrecord ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, docVersion, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = docVersion, mt_last_modified = transaction_timestamp();

  SELECT mt_version FROM public.mt_doc_purgedspendrecord into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_upsert_replicatedeventrecord(jsonb, character varying, uuid, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_upsert_replicatedeventrecord(doc jsonb, docdotnettype character varying, docid uuid, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version uuid;
BEGIN
INSERT INTO public.mt_doc_replicatedeventrecord ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, docVersion, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = docVersion, mt_last_modified = transaction_timestamp();

  SELECT mt_version FROM public.mt_doc_replicatedeventrecord into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_upsert_runactivity(jsonb, character varying, uuid, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_upsert_runactivity(doc jsonb, docdotnettype character varying, docid uuid, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version uuid;
BEGIN
INSERT INTO public.mt_doc_runactivity ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, docVersion, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = docVersion, mt_last_modified = transaction_timestamp();

  SELECT mt_version FROM public.mt_doc_runactivity into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_upsert_rundetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_upsert_rundetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN

SELECT version into current_version FROM public.mt_streams WHERE id = docId ;
if revision = 0 then
  if current_version is not null then
    revision = current_version;
  else
    revision = 1;
  end if;
else
  if current_version is not null then
    if current_version > revision then
      return 0;
    end if;
  end if;
end if;

INSERT INTO public.mt_doc_rundetails ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp() where revision > public.mt_doc_rundetails.mt_version;

  SELECT mt_version into final_version FROM public.mt_doc_rundetails WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_upsert_runlistitem(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_upsert_runlistitem(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN

SELECT version into current_version FROM public.mt_streams WHERE id = docId ;
if revision = 0 then
  if current_version is not null then
    revision = current_version;
  else
    revision = 1;
  end if;
else
  if current_version is not null then
    if current_version > revision then
      return 0;
    end if;
  end if;
end if;

INSERT INTO public.mt_doc_runlistitem ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp() where revision > public.mt_doc_runlistitem.mt_version;

  SELECT mt_version into final_version FROM public.mt_doc_runlistitem WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_upsert_runskillsyncposition(jsonb, character varying, uuid, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_upsert_runskillsyncposition(doc jsonb, docdotnettype character varying, docid uuid, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version uuid;
BEGIN
INSERT INTO public.mt_doc_runskillsyncposition ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, docVersion, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = docVersion, mt_last_modified = transaction_timestamp();

  SELECT mt_version FROM public.mt_doc_runskillsyncposition into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_upsert_taskdetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_upsert_taskdetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN

SELECT version into current_version FROM public.mt_streams WHERE id = docId ;
if revision = 0 then
  if current_version is not null then
    revision = current_version;
  else
    revision = 1;
  end if;
else
  if current_version is not null then
    if current_version > revision then
      return 0;
    end if;
  end if;
end if;

INSERT INTO public.mt_doc_taskdetails ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp() where revision > public.mt_doc_taskdetails.mt_version;

  SELECT mt_version into final_version FROM public.mt_doc_taskdetails WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_upsert_taskholderclaimhold(jsonb, character varying, character varying, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_upsert_taskholderclaimhold(doc jsonb, docdotnettype character varying, docid character varying, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version uuid;
BEGIN
INSERT INTO public.mt_doc_taskholderclaimhold ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, docVersion, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = docVersion, mt_last_modified = transaction_timestamp();

  SELECT mt_version FROM public.mt_doc_taskholderclaimhold into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_upsert_taskholderreleasepending(jsonb, character varying, character varying, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_upsert_taskholderreleasepending(doc jsonb, docdotnettype character varying, docid character varying, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version uuid;
BEGIN
INSERT INTO public.mt_doc_taskholderreleasepending ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, docVersion, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = docVersion, mt_last_modified = transaction_timestamp();

  SELECT mt_version FROM public.mt_doc_taskholderreleasepending into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_upsert_tasklease(jsonb, character varying, uuid, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_upsert_tasklease(doc jsonb, docdotnettype character varying, docid uuid, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version uuid;
BEGIN
INSERT INTO public.mt_doc_tasklease ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, docVersion, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = docVersion, mt_last_modified = transaction_timestamp();

  SELECT mt_version FROM public.mt_doc_tasklease into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_upsert_tasklistitem(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_upsert_tasklistitem(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN

SELECT version into current_version FROM public.mt_streams WHERE id = docId ;
if revision = 0 then
  if current_version is not null then
    revision = current_version;
  else
    revision = 1;
  end if;
else
  if current_version is not null then
    if current_version > revision then
      return 0;
    end if;
  end if;
end if;

INSERT INTO public.mt_doc_tasklistitem ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp() where revision > public.mt_doc_tasklistitem.mt_version;

  SELECT mt_version into final_version FROM public.mt_doc_tasklistitem WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_upsert_tasktrackerassignmirrorpending(jsonb, character varying, character varying, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_upsert_tasktrackerassignmirrorpending(doc jsonb, docdotnettype character varying, docid character varying, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version uuid;
BEGIN
INSERT INTO public.mt_doc_tasktrackerassignmirrorpending ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, docVersion, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = docVersion, mt_last_modified = transaction_timestamp();

  SELECT mt_version FROM public.mt_doc_tasktrackerassignmirrorpending into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_upsert_tasktrackerreleasemirrorpending(jsonb, character varying, character varying, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_upsert_tasktrackerreleasemirrorpending(doc jsonb, docdotnettype character varying, docid character varying, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version uuid;
BEGIN
INSERT INTO public.mt_doc_tasktrackerreleasemirrorpending ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, docVersion, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = docVersion, mt_last_modified = transaction_timestamp();

  SELECT mt_version FROM public.mt_doc_tasktrackerreleasemirrorpending into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_upsert_trackerclaimhold(jsonb, character varying, character varying, uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_upsert_trackerclaimhold(doc jsonb, docdotnettype character varying, docid character varying, docversion uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version uuid;
BEGIN
INSERT INTO public.mt_doc_trackerclaimhold ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, docVersion, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = docVersion, mt_last_modified = transaction_timestamp();

  SELECT mt_version FROM public.mt_doc_trackerclaimhold into final_version WHERE id = docId ;
  RETURN final_version;
END;
$$;


--
-- Name: mt_upsert_unverifiedledgerwritedetails(jsonb, character varying, uuid, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mt_upsert_unverifiedledgerwritedetails(doc jsonb, docdotnettype character varying, docid uuid, revision integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  final_version INTEGER;
  current_version INTEGER;
BEGIN

SELECT version into current_version FROM public.mt_streams WHERE id = docId ;
if revision = 0 then
  if current_version is not null then
    revision = current_version;
  else
    revision = 1;
  end if;
else
  if current_version is not null then
    if current_version > revision then
      return 0;
    end if;
  end if;
end if;

INSERT INTO public.mt_doc_unverifiedledgerwritedetails ("data", "mt_dotnet_type", "id", "mt_version", mt_last_modified) VALUES (doc, docDotNetType, docId, revision, transaction_timestamp())
  ON CONFLICT (id)
  DO UPDATE SET "data" = doc, "mt_dotnet_type" = docDotNetType, "mt_version" = revision, mt_last_modified = transaction_timestamp() where revision > public.mt_doc_unverifiedledgerwritedetails.mt_version;

  SELECT mt_version into final_version FROM public.mt_doc_unverifiedledgerwritedetails WHERE id = docId ;
  RETURN final_version;
END;
$$;


SET default_tablespace = '';

SET default_table_access_method = heap;

--
-- Name: mt_doc_autoprreviewdefaultadoption; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.mt_doc_autoprreviewdefaultadoption (
    id uuid NOT NULL,
    data jsonb NOT NULL,
    mt_last_modified timestamp with time zone DEFAULT transaction_timestamp(),
    mt_version uuid DEFAULT (md5(((random())::text || (clock_timestamp())::text)))::uuid NOT NULL,
    mt_dotnet_type character varying
);


--
-- Name: mt_doc_cleanbasegateverdict; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.mt_doc_cleanbasegateverdict (
    id character varying NOT NULL,
    data jsonb NOT NULL,
    mt_last_modified timestamp with time zone DEFAULT transaction_timestamp(),
    mt_version uuid DEFAULT (md5(((random())::text || (clock_timestamp())::text)))::uuid NOT NULL,
    mt_dotnet_type character varying
);


--
-- Name: mt_doc_connectiondetails; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.mt_doc_connectiondetails (
    id uuid NOT NULL,
    data jsonb NOT NULL,
    mt_last_modified timestamp with time zone DEFAULT transaction_timestamp(),
    mt_dotnet_type character varying,
    mt_version integer DEFAULT 0 NOT NULL
);


--
-- Name: mt_doc_courierdayspawncounter; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.mt_doc_courierdayspawncounter (
    id uuid NOT NULL,
    data jsonb NOT NULL,
    mt_last_modified timestamp with time zone DEFAULT transaction_timestamp(),
    mt_version uuid DEFAULT (md5(((random())::text || (clock_timestamp())::text)))::uuid NOT NULL,
    mt_dotnet_type character varying
);


--
-- Name: mt_doc_courierrundetails; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.mt_doc_courierrundetails (
    id uuid NOT NULL,
    data jsonb NOT NULL,
    mt_last_modified timestamp with time zone DEFAULT transaction_timestamp(),
    mt_dotnet_type character varying,
    mt_version integer DEFAULT 0 NOT NULL
);


--
-- Name: mt_doc_deadletterevent; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.mt_doc_deadletterevent (
    id uuid NOT NULL,
    data jsonb NOT NULL,
    mt_last_modified timestamp with time zone DEFAULT transaction_timestamp(),
    mt_version uuid DEFAULT (md5(((random())::text || (clock_timestamp())::text)))::uuid NOT NULL,
    mt_dotnet_type character varying
);


--
-- Name: mt_doc_decisiondetails; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.mt_doc_decisiondetails (
    id uuid NOT NULL,
    data jsonb NOT NULL,
    mt_last_modified timestamp with time zone DEFAULT transaction_timestamp(),
    mt_dotnet_type character varying,
    mt_version integer DEFAULT 0 NOT NULL
);


--
-- Name: mt_doc_epicdetails; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.mt_doc_epicdetails (
    id uuid NOT NULL,
    data jsonb NOT NULL,
    mt_last_modified timestamp with time zone DEFAULT transaction_timestamp(),
    mt_dotnet_type character varying,
    mt_version integer DEFAULT 0 NOT NULL
);


--
-- Name: mt_doc_eventcatchupinboxcursor; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.mt_doc_eventcatchupinboxcursor (
    id uuid NOT NULL,
    data jsonb NOT NULL,
    mt_last_modified timestamp with time zone DEFAULT transaction_timestamp(),
    mt_version uuid DEFAULT (md5(((random())::text || (clock_timestamp())::text)))::uuid NOT NULL,
    mt_dotnet_type character varying
);


--
-- Name: mt_doc_eventcatchuprequest; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.mt_doc_eventcatchuprequest (
    id uuid NOT NULL,
    data jsonb NOT NULL,
    mt_last_modified timestamp with time zone DEFAULT transaction_timestamp(),
    mt_version uuid DEFAULT (md5(((random())::text || (clock_timestamp())::text)))::uuid NOT NULL,
    mt_dotnet_type character varying
);


--
-- Name: mt_doc_eventoriginprogress; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.mt_doc_eventoriginprogress (
    id uuid NOT NULL,
    data jsonb NOT NULL,
    mt_last_modified timestamp with time zone DEFAULT transaction_timestamp(),
    mt_version uuid DEFAULT (md5(((random())::text || (clock_timestamp())::text)))::uuid NOT NULL,
    mt_dotnet_type character varying
);


--
-- Name: mt_doc_eventreplicationinboxcursor; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.mt_doc_eventreplicationinboxcursor (
    id uuid NOT NULL,
    data jsonb NOT NULL,
    mt_last_modified timestamp with time zone DEFAULT transaction_timestamp(),
    mt_version uuid DEFAULT (md5(((random())::text || (clock_timestamp())::text)))::uuid NOT NULL,
    mt_dotnet_type character varying
);


--
-- Name: mt_doc_eventreplicationoutboxposition; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.mt_doc_eventreplicationoutboxposition (
    id uuid NOT NULL,
    data jsonb NOT NULL,
    mt_last_modified timestamp with time zone DEFAULT transaction_timestamp(),
    mt_version uuid DEFAULT (md5(((random())::text || (clock_timestamp())::text)))::uuid NOT NULL,
    mt_dotnet_type character varying
);


--
-- Name: mt_doc_fleetprojectreconcile; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.mt_doc_fleetprojectreconcile (
    id uuid NOT NULL,
    data jsonb NOT NULL,
    mt_last_modified timestamp with time zone DEFAULT transaction_timestamp(),
    mt_version uuid DEFAULT (md5(((random())::text || (clock_timestamp())::text)))::uuid NOT NULL,
    mt_dotnet_type character varying
);


--
-- Name: mt_doc_heldreplicatedeventrecord; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.mt_doc_heldreplicatedeventrecord (
    id uuid NOT NULL,
    data jsonb NOT NULL,
    mt_last_modified timestamp with time zone DEFAULT transaction_timestamp(),
    mt_version uuid DEFAULT (md5(((random())::text || (clock_timestamp())::text)))::uuid NOT NULL,
    mt_dotnet_type character varying
);


--
-- Name: mt_doc_ideadetails; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.mt_doc_ideadetails (
    id uuid NOT NULL,
    data jsonb NOT NULL,
    mt_last_modified timestamp with time zone DEFAULT transaction_timestamp(),
    mt_dotnet_type character varying,
    mt_version integer DEFAULT 0 NOT NULL
);


--
-- Name: mt_doc_invitedetails; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.mt_doc_invitedetails (
    id uuid NOT NULL,
    data jsonb NOT NULL,
    mt_last_modified timestamp with time zone DEFAULT transaction_timestamp(),
    mt_dotnet_type character varying,
    mt_version integer DEFAULT 0 NOT NULL
);


--
-- Name: mt_doc_learningdetails; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.mt_doc_learningdetails (
    id uuid NOT NULL,
    data jsonb NOT NULL,
    mt_last_modified timestamp with time zone DEFAULT transaction_timestamp(),
    mt_dotnet_type character varying,
    mt_version integer DEFAULT 0 NOT NULL
);


--
-- Name: mt_doc_legacymessageadoptiondetails; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.mt_doc_legacymessageadoptiondetails (
    id uuid NOT NULL,
    data jsonb NOT NULL,
    mt_last_modified timestamp with time zone DEFAULT transaction_timestamp(),
    mt_dotnet_type character varying,
    mt_version integer DEFAULT 0 NOT NULL
);


--
-- Name: mt_doc_messagedetails; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.mt_doc_messagedetails (
    id uuid NOT NULL,
    data jsonb NOT NULL,
    mt_last_modified timestamp with time zone DEFAULT transaction_timestamp(),
    mt_dotnet_type character varying,
    mt_version integer DEFAULT 0 NOT NULL
);


--
-- Name: mt_doc_messageinboxdetails; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.mt_doc_messageinboxdetails (
    id uuid NOT NULL,
    data jsonb NOT NULL,
    mt_last_modified timestamp with time zone DEFAULT transaction_timestamp(),
    mt_dotnet_type character varying,
    mt_version integer DEFAULT 0 NOT NULL
);


--
-- Name: mt_doc_nodedetails; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.mt_doc_nodedetails (
    id uuid NOT NULL,
    data jsonb NOT NULL,
    mt_last_modified timestamp with time zone DEFAULT transaction_timestamp(),
    mt_dotnet_type character varying,
    mt_version integer DEFAULT 0 NOT NULL
);


--
-- Name: mt_doc_nodedispatchload; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.mt_doc_nodedispatchload (
    id uuid NOT NULL,
    data jsonb NOT NULL,
    mt_last_modified timestamp with time zone DEFAULT transaction_timestamp(),
    mt_version uuid DEFAULT (md5(((random())::text || (clock_timestamp())::text)))::uuid NOT NULL,
    mt_dotnet_type character varying
);


--
-- Name: mt_doc_observedreviewmention; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.mt_doc_observedreviewmention (
    id character varying NOT NULL,
    data jsonb NOT NULL,
    mt_last_modified timestamp with time zone DEFAULT transaction_timestamp(),
    mt_version uuid DEFAULT (md5(((random())::text || (clock_timestamp())::text)))::uuid NOT NULL,
    mt_dotnet_type character varying
);


--
-- Name: mt_doc_observedreviewrequest; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.mt_doc_observedreviewrequest (
    id character varying NOT NULL,
    data jsonb NOT NULL,
    mt_last_modified timestamp with time zone DEFAULT transaction_timestamp(),
    mt_version uuid DEFAULT (md5(((random())::text || (clock_timestamp())::text)))::uuid NOT NULL,
    mt_dotnet_type character varying
);


--
-- Name: mt_doc_orchestratorfeedcursor; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.mt_doc_orchestratorfeedcursor (
    id uuid NOT NULL,
    data jsonb NOT NULL,
    mt_last_modified timestamp with time zone DEFAULT transaction_timestamp(),
    mt_version uuid DEFAULT (md5(((random())::text || (clock_timestamp())::text)))::uuid NOT NULL,
    mt_dotnet_type character varying
);


--
-- Name: mt_doc_orchestratorfeeddrainlease; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.mt_doc_orchestratorfeeddrainlease (
    id uuid NOT NULL,
    data jsonb NOT NULL,
    mt_last_modified timestamp with time zone DEFAULT transaction_timestamp(),
    mt_version uuid DEFAULT (md5(((random())::text || (clock_timestamp())::text)))::uuid NOT NULL,
    mt_dotnet_type character varying
);


--
-- Name: mt_doc_orchestratorpresencedetails; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.mt_doc_orchestratorpresencedetails (
    id uuid NOT NULL,
    data jsonb NOT NULL,
    mt_last_modified timestamp with time zone DEFAULT transaction_timestamp(),
    mt_dotnet_type character varying,
    mt_version integer DEFAULT 0 NOT NULL
);


--
-- Name: mt_doc_ownerdetails; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.mt_doc_ownerdetails (
    id uuid NOT NULL,
    data jsonb NOT NULL,
    mt_last_modified timestamp with time zone DEFAULT transaction_timestamp(),
    mt_dotnet_type character varying,
    mt_version integer DEFAULT 0 NOT NULL
);


--
-- Name: mt_doc_projectdetails; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.mt_doc_projectdetails (
    id uuid NOT NULL,
    data jsonb NOT NULL,
    mt_last_modified timestamp with time zone DEFAULT transaction_timestamp(),
    mt_dotnet_type character varying,
    mt_version integer DEFAULT 0 NOT NULL
);


--
-- Name: mt_doc_projectgithubmembers; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.mt_doc_projectgithubmembers (
    id uuid NOT NULL,
    data jsonb NOT NULL,
    mt_last_modified timestamp with time zone DEFAULT transaction_timestamp(),
    mt_dotnet_type character varying,
    mt_version integer DEFAULT 0 NOT NULL
);


--
-- Name: mt_doc_promptaddendasyncposition; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.mt_doc_promptaddendasyncposition (
    id uuid NOT NULL,
    data jsonb NOT NULL,
    mt_last_modified timestamp with time zone DEFAULT transaction_timestamp(),
    mt_version uuid DEFAULT (md5(((random())::text || (clock_timestamp())::text)))::uuid NOT NULL,
    mt_dotnet_type character varying
);


--
-- Name: mt_doc_purgedspendrecord; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.mt_doc_purgedspendrecord (
    id uuid NOT NULL,
    data jsonb NOT NULL,
    mt_last_modified timestamp with time zone DEFAULT transaction_timestamp(),
    mt_version uuid DEFAULT (md5(((random())::text || (clock_timestamp())::text)))::uuid NOT NULL,
    mt_dotnet_type character varying
);


--
-- Name: mt_doc_replicatedeventrecord; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.mt_doc_replicatedeventrecord (
    id uuid NOT NULL,
    data jsonb NOT NULL,
    mt_last_modified timestamp with time zone DEFAULT transaction_timestamp(),
    mt_version uuid DEFAULT (md5(((random())::text || (clock_timestamp())::text)))::uuid NOT NULL,
    mt_dotnet_type character varying
);


--
-- Name: mt_doc_runactivity; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.mt_doc_runactivity (
    id uuid NOT NULL,
    data jsonb NOT NULL,
    mt_last_modified timestamp with time zone DEFAULT transaction_timestamp(),
    mt_version uuid DEFAULT (md5(((random())::text || (clock_timestamp())::text)))::uuid NOT NULL,
    mt_dotnet_type character varying
);


--
-- Name: mt_doc_rundetails; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.mt_doc_rundetails (
    id uuid NOT NULL,
    data jsonb NOT NULL,
    mt_last_modified timestamp with time zone DEFAULT transaction_timestamp(),
    mt_dotnet_type character varying,
    mt_version integer DEFAULT 0 NOT NULL
);


--
-- Name: mt_doc_runlistitem; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.mt_doc_runlistitem (
    id uuid NOT NULL,
    data jsonb NOT NULL,
    mt_last_modified timestamp with time zone DEFAULT transaction_timestamp(),
    mt_dotnet_type character varying,
    mt_version integer DEFAULT 0 NOT NULL
);


--
-- Name: mt_doc_runskillsyncposition; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.mt_doc_runskillsyncposition (
    id uuid NOT NULL,
    data jsonb NOT NULL,
    mt_last_modified timestamp with time zone DEFAULT transaction_timestamp(),
    mt_version uuid DEFAULT (md5(((random())::text || (clock_timestamp())::text)))::uuid NOT NULL,
    mt_dotnet_type character varying
);


--
-- Name: mt_doc_taskdetails; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.mt_doc_taskdetails (
    id uuid NOT NULL,
    data jsonb NOT NULL,
    mt_last_modified timestamp with time zone DEFAULT transaction_timestamp(),
    mt_dotnet_type character varying,
    mt_version integer DEFAULT 0 NOT NULL
);


--
-- Name: mt_doc_taskholderclaimhold; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.mt_doc_taskholderclaimhold (
    id character varying NOT NULL,
    data jsonb NOT NULL,
    mt_last_modified timestamp with time zone DEFAULT transaction_timestamp(),
    mt_version uuid DEFAULT (md5(((random())::text || (clock_timestamp())::text)))::uuid NOT NULL,
    mt_dotnet_type character varying
);


--
-- Name: mt_doc_taskholderreleasepending; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.mt_doc_taskholderreleasepending (
    id character varying NOT NULL,
    data jsonb NOT NULL,
    mt_last_modified timestamp with time zone DEFAULT transaction_timestamp(),
    mt_version uuid DEFAULT (md5(((random())::text || (clock_timestamp())::text)))::uuid NOT NULL,
    mt_dotnet_type character varying
);


--
-- Name: mt_doc_tasklease; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.mt_doc_tasklease (
    id uuid NOT NULL,
    data jsonb NOT NULL,
    mt_last_modified timestamp with time zone DEFAULT transaction_timestamp(),
    mt_version uuid DEFAULT (md5(((random())::text || (clock_timestamp())::text)))::uuid NOT NULL,
    mt_dotnet_type character varying
);


--
-- Name: mt_doc_tasklistitem; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.mt_doc_tasklistitem (
    id uuid NOT NULL,
    data jsonb NOT NULL,
    mt_last_modified timestamp with time zone DEFAULT transaction_timestamp(),
    mt_dotnet_type character varying,
    mt_version integer DEFAULT 0 NOT NULL
);


--
-- Name: mt_doc_tasktrackerassignmirrorpending; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.mt_doc_tasktrackerassignmirrorpending (
    id character varying NOT NULL,
    data jsonb NOT NULL,
    mt_last_modified timestamp with time zone DEFAULT transaction_timestamp(),
    mt_version uuid DEFAULT (md5(((random())::text || (clock_timestamp())::text)))::uuid NOT NULL,
    mt_dotnet_type character varying
);


--
-- Name: mt_doc_tasktrackerreleasemirrorpending; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.mt_doc_tasktrackerreleasemirrorpending (
    id character varying NOT NULL,
    data jsonb NOT NULL,
    mt_last_modified timestamp with time zone DEFAULT transaction_timestamp(),
    mt_version uuid DEFAULT (md5(((random())::text || (clock_timestamp())::text)))::uuid NOT NULL,
    mt_dotnet_type character varying
);


--
-- Name: mt_doc_trackerclaimhold; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.mt_doc_trackerclaimhold (
    id character varying NOT NULL,
    data jsonb NOT NULL,
    mt_last_modified timestamp with time zone DEFAULT transaction_timestamp(),
    mt_version uuid DEFAULT (md5(((random())::text || (clock_timestamp())::text)))::uuid NOT NULL,
    mt_dotnet_type character varying
);


--
-- Name: mt_doc_unverifiedledgerwritedetails; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.mt_doc_unverifiedledgerwritedetails (
    id uuid NOT NULL,
    data jsonb NOT NULL,
    mt_last_modified timestamp with time zone DEFAULT transaction_timestamp(),
    mt_dotnet_type character varying,
    mt_version integer DEFAULT 0 NOT NULL
);


--
-- Name: mt_event_progression; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.mt_event_progression (
    name character varying NOT NULL,
    last_seq_id bigint,
    last_updated timestamp with time zone DEFAULT transaction_timestamp()
);


--
-- Name: mt_events; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.mt_events (
    seq_id bigint NOT NULL,
    id uuid NOT NULL,
    stream_id uuid,
    version bigint NOT NULL,
    data jsonb NOT NULL,
    type character varying(500) NOT NULL,
    "timestamp" timestamp with time zone DEFAULT '2026-08-17 01:06:22.828054+00'::timestamp with time zone NOT NULL,
    tenant_id character varying DEFAULT '*DEFAULT*'::character varying,
    mt_dotnet_type character varying,
    is_archived boolean DEFAULT false,
    headers jsonb
);


--
-- Name: mt_events_sequence; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.mt_events_sequence
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: mt_events_sequence; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.mt_events_sequence OWNED BY public.mt_events.seq_id;


--
-- Name: mt_streams; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.mt_streams (
    id uuid NOT NULL,
    type character varying,
    version bigint,
    "timestamp" timestamp with time zone DEFAULT now() NOT NULL,
    snapshot jsonb,
    snapshot_version integer,
    created timestamp with time zone DEFAULT now() NOT NULL,
    tenant_id character varying DEFAULT '*DEFAULT*'::character varying,
    is_archived boolean DEFAULT false
);


--
-- Name: wolverine_agent_restrictions; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.wolverine_agent_restrictions (
    id uuid NOT NULL,
    uri character varying NOT NULL,
    type character varying NOT NULL,
    node integer DEFAULT 0 NOT NULL
);


--
-- Name: wolverine_control_queue; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.wolverine_control_queue (
    id uuid NOT NULL,
    message_type character varying NOT NULL,
    node_id uuid NOT NULL,
    body bytea NOT NULL,
    posted timestamp with time zone DEFAULT now() NOT NULL,
    expires timestamp with time zone
);


--
-- Name: wolverine_dead_letters; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.wolverine_dead_letters (
    id uuid NOT NULL,
    execution_time timestamp with time zone,
    body bytea NOT NULL,
    message_type character varying NOT NULL,
    received_at character varying,
    source character varying,
    exception_type character varying,
    exception_message character varying,
    sent_at timestamp with time zone,
    replayable boolean
);


--
-- Name: wolverine_incoming_envelopes; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.wolverine_incoming_envelopes (
    id uuid NOT NULL,
    status character varying NOT NULL,
    owner_id integer NOT NULL,
    execution_time timestamp with time zone,
    attempts integer DEFAULT 0,
    body bytea NOT NULL,
    message_type character varying NOT NULL,
    received_at character varying,
    keep_until timestamp with time zone
);


--
-- Name: wolverine_node_assignments; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.wolverine_node_assignments (
    id character varying NOT NULL,
    node_id uuid,
    started timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: wolverine_node_records; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.wolverine_node_records (
    id integer NOT NULL,
    node_number integer NOT NULL,
    event_name character varying NOT NULL,
    "timestamp" timestamp with time zone DEFAULT now() NOT NULL,
    description character varying
);


--
-- Name: wolverine_node_records_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.wolverine_node_records_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: wolverine_node_records_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.wolverine_node_records_id_seq OWNED BY public.wolverine_node_records.id;


--
-- Name: wolverine_nodes; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.wolverine_nodes (
    id uuid NOT NULL,
    node_number integer NOT NULL,
    description character varying NOT NULL,
    uri character varying NOT NULL,
    started timestamp with time zone DEFAULT now() NOT NULL,
    health_check timestamp with time zone DEFAULT now() NOT NULL,
    version character varying,
    capabilities text[]
);


--
-- Name: wolverine_nodes_node_number_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.wolverine_nodes_node_number_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: wolverine_nodes_node_number_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.wolverine_nodes_node_number_seq OWNED BY public.wolverine_nodes.node_number;


--
-- Name: wolverine_outgoing_envelopes; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.wolverine_outgoing_envelopes (
    id uuid NOT NULL,
    owner_id integer NOT NULL,
    destination character varying NOT NULL,
    deliver_by timestamp with time zone,
    body bytea NOT NULL,
    attempts integer DEFAULT 0,
    message_type character varying NOT NULL
);


--
-- Name: wolverine_node_records id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.wolverine_node_records ALTER COLUMN id SET DEFAULT nextval('public.wolverine_node_records_id_seq'::regclass);


--
-- Name: wolverine_nodes node_number; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.wolverine_nodes ALTER COLUMN node_number SET DEFAULT nextval('public.wolverine_nodes_node_number_seq'::regclass);


--
-- Name: mt_event_progression pk_mt_event_progression; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mt_event_progression
    ADD CONSTRAINT pk_mt_event_progression PRIMARY KEY (name);


--
-- Name: mt_doc_autoprreviewdefaultadoption pkey_mt_doc_autoprreviewdefaultadoption_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mt_doc_autoprreviewdefaultadoption
    ADD CONSTRAINT pkey_mt_doc_autoprreviewdefaultadoption_id PRIMARY KEY (id);


--
-- Name: mt_doc_cleanbasegateverdict pkey_mt_doc_cleanbasegateverdict_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mt_doc_cleanbasegateverdict
    ADD CONSTRAINT pkey_mt_doc_cleanbasegateverdict_id PRIMARY KEY (id);


--
-- Name: mt_doc_connectiondetails pkey_mt_doc_connectiondetails_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mt_doc_connectiondetails
    ADD CONSTRAINT pkey_mt_doc_connectiondetails_id PRIMARY KEY (id);


--
-- Name: mt_doc_courierdayspawncounter pkey_mt_doc_courierdayspawncounter_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mt_doc_courierdayspawncounter
    ADD CONSTRAINT pkey_mt_doc_courierdayspawncounter_id PRIMARY KEY (id);


--
-- Name: mt_doc_courierrundetails pkey_mt_doc_courierrundetails_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mt_doc_courierrundetails
    ADD CONSTRAINT pkey_mt_doc_courierrundetails_id PRIMARY KEY (id);


--
-- Name: mt_doc_deadletterevent pkey_mt_doc_deadletterevent_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mt_doc_deadletterevent
    ADD CONSTRAINT pkey_mt_doc_deadletterevent_id PRIMARY KEY (id);


--
-- Name: mt_doc_decisiondetails pkey_mt_doc_decisiondetails_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mt_doc_decisiondetails
    ADD CONSTRAINT pkey_mt_doc_decisiondetails_id PRIMARY KEY (id);


--
-- Name: mt_doc_epicdetails pkey_mt_doc_epicdetails_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mt_doc_epicdetails
    ADD CONSTRAINT pkey_mt_doc_epicdetails_id PRIMARY KEY (id);


--
-- Name: mt_doc_eventcatchupinboxcursor pkey_mt_doc_eventcatchupinboxcursor_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mt_doc_eventcatchupinboxcursor
    ADD CONSTRAINT pkey_mt_doc_eventcatchupinboxcursor_id PRIMARY KEY (id);


--
-- Name: mt_doc_eventcatchuprequest pkey_mt_doc_eventcatchuprequest_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mt_doc_eventcatchuprequest
    ADD CONSTRAINT pkey_mt_doc_eventcatchuprequest_id PRIMARY KEY (id);


--
-- Name: mt_doc_eventoriginprogress pkey_mt_doc_eventoriginprogress_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mt_doc_eventoriginprogress
    ADD CONSTRAINT pkey_mt_doc_eventoriginprogress_id PRIMARY KEY (id);


--
-- Name: mt_doc_eventreplicationinboxcursor pkey_mt_doc_eventreplicationinboxcursor_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mt_doc_eventreplicationinboxcursor
    ADD CONSTRAINT pkey_mt_doc_eventreplicationinboxcursor_id PRIMARY KEY (id);


--
-- Name: mt_doc_eventreplicationoutboxposition pkey_mt_doc_eventreplicationoutboxposition_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mt_doc_eventreplicationoutboxposition
    ADD CONSTRAINT pkey_mt_doc_eventreplicationoutboxposition_id PRIMARY KEY (id);


--
-- Name: mt_doc_fleetprojectreconcile pkey_mt_doc_fleetprojectreconcile_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mt_doc_fleetprojectreconcile
    ADD CONSTRAINT pkey_mt_doc_fleetprojectreconcile_id PRIMARY KEY (id);


--
-- Name: mt_doc_heldreplicatedeventrecord pkey_mt_doc_heldreplicatedeventrecord_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mt_doc_heldreplicatedeventrecord
    ADD CONSTRAINT pkey_mt_doc_heldreplicatedeventrecord_id PRIMARY KEY (id);


--
-- Name: mt_doc_ideadetails pkey_mt_doc_ideadetails_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mt_doc_ideadetails
    ADD CONSTRAINT pkey_mt_doc_ideadetails_id PRIMARY KEY (id);


--
-- Name: mt_doc_invitedetails pkey_mt_doc_invitedetails_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mt_doc_invitedetails
    ADD CONSTRAINT pkey_mt_doc_invitedetails_id PRIMARY KEY (id);


--
-- Name: mt_doc_learningdetails pkey_mt_doc_learningdetails_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mt_doc_learningdetails
    ADD CONSTRAINT pkey_mt_doc_learningdetails_id PRIMARY KEY (id);


--
-- Name: mt_doc_legacymessageadoptiondetails pkey_mt_doc_legacymessageadoptiondetails_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mt_doc_legacymessageadoptiondetails
    ADD CONSTRAINT pkey_mt_doc_legacymessageadoptiondetails_id PRIMARY KEY (id);


--
-- Name: mt_doc_messagedetails pkey_mt_doc_messagedetails_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mt_doc_messagedetails
    ADD CONSTRAINT pkey_mt_doc_messagedetails_id PRIMARY KEY (id);


--
-- Name: mt_doc_messageinboxdetails pkey_mt_doc_messageinboxdetails_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mt_doc_messageinboxdetails
    ADD CONSTRAINT pkey_mt_doc_messageinboxdetails_id PRIMARY KEY (id);


--
-- Name: mt_doc_nodedetails pkey_mt_doc_nodedetails_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mt_doc_nodedetails
    ADD CONSTRAINT pkey_mt_doc_nodedetails_id PRIMARY KEY (id);


--
-- Name: mt_doc_nodedispatchload pkey_mt_doc_nodedispatchload_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mt_doc_nodedispatchload
    ADD CONSTRAINT pkey_mt_doc_nodedispatchload_id PRIMARY KEY (id);


--
-- Name: mt_doc_observedreviewmention pkey_mt_doc_observedreviewmention_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mt_doc_observedreviewmention
    ADD CONSTRAINT pkey_mt_doc_observedreviewmention_id PRIMARY KEY (id);


--
-- Name: mt_doc_observedreviewrequest pkey_mt_doc_observedreviewrequest_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mt_doc_observedreviewrequest
    ADD CONSTRAINT pkey_mt_doc_observedreviewrequest_id PRIMARY KEY (id);


--
-- Name: mt_doc_orchestratorfeedcursor pkey_mt_doc_orchestratorfeedcursor_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mt_doc_orchestratorfeedcursor
    ADD CONSTRAINT pkey_mt_doc_orchestratorfeedcursor_id PRIMARY KEY (id);


--
-- Name: mt_doc_orchestratorfeeddrainlease pkey_mt_doc_orchestratorfeeddrainlease_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mt_doc_orchestratorfeeddrainlease
    ADD CONSTRAINT pkey_mt_doc_orchestratorfeeddrainlease_id PRIMARY KEY (id);


--
-- Name: mt_doc_orchestratorpresencedetails pkey_mt_doc_orchestratorpresencedetails_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mt_doc_orchestratorpresencedetails
    ADD CONSTRAINT pkey_mt_doc_orchestratorpresencedetails_id PRIMARY KEY (id);


--
-- Name: mt_doc_ownerdetails pkey_mt_doc_ownerdetails_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mt_doc_ownerdetails
    ADD CONSTRAINT pkey_mt_doc_ownerdetails_id PRIMARY KEY (id);


--
-- Name: mt_doc_projectdetails pkey_mt_doc_projectdetails_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mt_doc_projectdetails
    ADD CONSTRAINT pkey_mt_doc_projectdetails_id PRIMARY KEY (id);


--
-- Name: mt_doc_projectgithubmembers pkey_mt_doc_projectgithubmembers_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mt_doc_projectgithubmembers
    ADD CONSTRAINT pkey_mt_doc_projectgithubmembers_id PRIMARY KEY (id);


--
-- Name: mt_doc_promptaddendasyncposition pkey_mt_doc_promptaddendasyncposition_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mt_doc_promptaddendasyncposition
    ADD CONSTRAINT pkey_mt_doc_promptaddendasyncposition_id PRIMARY KEY (id);


--
-- Name: mt_doc_purgedspendrecord pkey_mt_doc_purgedspendrecord_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mt_doc_purgedspendrecord
    ADD CONSTRAINT pkey_mt_doc_purgedspendrecord_id PRIMARY KEY (id);


--
-- Name: mt_doc_replicatedeventrecord pkey_mt_doc_replicatedeventrecord_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mt_doc_replicatedeventrecord
    ADD CONSTRAINT pkey_mt_doc_replicatedeventrecord_id PRIMARY KEY (id);


--
-- Name: mt_doc_runactivity pkey_mt_doc_runactivity_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mt_doc_runactivity
    ADD CONSTRAINT pkey_mt_doc_runactivity_id PRIMARY KEY (id);


--
-- Name: mt_doc_rundetails pkey_mt_doc_rundetails_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mt_doc_rundetails
    ADD CONSTRAINT pkey_mt_doc_rundetails_id PRIMARY KEY (id);


--
-- Name: mt_doc_runlistitem pkey_mt_doc_runlistitem_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mt_doc_runlistitem
    ADD CONSTRAINT pkey_mt_doc_runlistitem_id PRIMARY KEY (id);


--
-- Name: mt_doc_runskillsyncposition pkey_mt_doc_runskillsyncposition_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mt_doc_runskillsyncposition
    ADD CONSTRAINT pkey_mt_doc_runskillsyncposition_id PRIMARY KEY (id);


--
-- Name: mt_doc_taskdetails pkey_mt_doc_taskdetails_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mt_doc_taskdetails
    ADD CONSTRAINT pkey_mt_doc_taskdetails_id PRIMARY KEY (id);


--
-- Name: mt_doc_taskholderclaimhold pkey_mt_doc_taskholderclaimhold_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mt_doc_taskholderclaimhold
    ADD CONSTRAINT pkey_mt_doc_taskholderclaimhold_id PRIMARY KEY (id);


--
-- Name: mt_doc_taskholderreleasepending pkey_mt_doc_taskholderreleasepending_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mt_doc_taskholderreleasepending
    ADD CONSTRAINT pkey_mt_doc_taskholderreleasepending_id PRIMARY KEY (id);


--
-- Name: mt_doc_tasklease pkey_mt_doc_tasklease_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mt_doc_tasklease
    ADD CONSTRAINT pkey_mt_doc_tasklease_id PRIMARY KEY (id);


--
-- Name: mt_doc_tasklistitem pkey_mt_doc_tasklistitem_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mt_doc_tasklistitem
    ADD CONSTRAINT pkey_mt_doc_tasklistitem_id PRIMARY KEY (id);


--
-- Name: mt_doc_tasktrackerassignmirrorpending pkey_mt_doc_tasktrackerassignmirrorpending_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mt_doc_tasktrackerassignmirrorpending
    ADD CONSTRAINT pkey_mt_doc_tasktrackerassignmirrorpending_id PRIMARY KEY (id);


--
-- Name: mt_doc_tasktrackerreleasemirrorpending pkey_mt_doc_tasktrackerreleasemirrorpending_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mt_doc_tasktrackerreleasemirrorpending
    ADD CONSTRAINT pkey_mt_doc_tasktrackerreleasemirrorpending_id PRIMARY KEY (id);


--
-- Name: mt_doc_trackerclaimhold pkey_mt_doc_trackerclaimhold_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mt_doc_trackerclaimhold
    ADD CONSTRAINT pkey_mt_doc_trackerclaimhold_id PRIMARY KEY (id);


--
-- Name: mt_doc_unverifiedledgerwritedetails pkey_mt_doc_unverifiedledgerwritedetails_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mt_doc_unverifiedledgerwritedetails
    ADD CONSTRAINT pkey_mt_doc_unverifiedledgerwritedetails_id PRIMARY KEY (id);


--
-- Name: mt_events pkey_mt_events_seq_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mt_events
    ADD CONSTRAINT pkey_mt_events_seq_id PRIMARY KEY (seq_id);


--
-- Name: mt_streams pkey_mt_streams_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mt_streams
    ADD CONSTRAINT pkey_mt_streams_id PRIMARY KEY (id);


--
-- Name: wolverine_agent_restrictions pkey_wolverine_agent_restrictions_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.wolverine_agent_restrictions
    ADD CONSTRAINT pkey_wolverine_agent_restrictions_id PRIMARY KEY (id);


--
-- Name: wolverine_control_queue pkey_wolverine_control_queue_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.wolverine_control_queue
    ADD CONSTRAINT pkey_wolverine_control_queue_id PRIMARY KEY (id);


--
-- Name: wolverine_dead_letters pkey_wolverine_dead_letters_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.wolverine_dead_letters
    ADD CONSTRAINT pkey_wolverine_dead_letters_id PRIMARY KEY (id);


--
-- Name: wolverine_incoming_envelopes pkey_wolverine_incoming_envelopes_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.wolverine_incoming_envelopes
    ADD CONSTRAINT pkey_wolverine_incoming_envelopes_id PRIMARY KEY (id);


--
-- Name: wolverine_node_assignments pkey_wolverine_node_assignments_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.wolverine_node_assignments
    ADD CONSTRAINT pkey_wolverine_node_assignments_id PRIMARY KEY (id);


--
-- Name: wolverine_node_records pkey_wolverine_node_records_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.wolverine_node_records
    ADD CONSTRAINT pkey_wolverine_node_records_id PRIMARY KEY (id);


--
-- Name: wolverine_nodes pkey_wolverine_nodes_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.wolverine_nodes
    ADD CONSTRAINT pkey_wolverine_nodes_id PRIMARY KEY (id);


--
-- Name: wolverine_outgoing_envelopes pkey_wolverine_outgoing_envelopes_id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.wolverine_outgoing_envelopes
    ADD CONSTRAINT pkey_wolverine_outgoing_envelopes_id PRIMARY KEY (id);


--
-- Name: mt_doc_decisiondetails_idx_scope_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX mt_doc_decisiondetails_idx_scope_id ON public.mt_doc_decisiondetails USING btree ((((data ->> 'scopeId'::text))::uuid));


--
-- Name: mt_doc_learningdetails_idx_scope_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX mt_doc_learningdetails_idx_scope_id ON public.mt_doc_learningdetails USING btree ((((data ->> 'scopeId'::text))::uuid));


--
-- Name: pk_mt_events_stream_and_version; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX pk_mt_events_stream_and_version ON public.mt_events USING btree (stream_id, version);


--
-- Name: mt_events fkey_mt_events_stream_id; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mt_events
    ADD CONSTRAINT fkey_mt_events_stream_id FOREIGN KEY (stream_id) REFERENCES public.mt_streams(id) ON DELETE CASCADE;


--
-- Name: wolverine_node_assignments fkey_wolverine_node_assignments_node_id; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.wolverine_node_assignments
    ADD CONSTRAINT fkey_wolverine_node_assignments_node_id FOREIGN KEY (node_id) REFERENCES public.wolverine_nodes(id) ON DELETE CASCADE;


--
-- PostgreSQL database dump complete
--

\unrestrict Xa23F1ktberAR6lTCao9kkoHuZF5FXQWHKYTfN0LWGOwDZ3sntDN4gpCZ1Ug4Ks

